using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Eto.Forms;
using Rhino;
using CoRhino.Pty;

namespace CoRhino.Panel
{
    [System.Runtime.InteropServices.Guid("4C13D5E0-9E5A-4F18-9C2A-1E9E1A7B3C02")]
    public class CoRhinoPanel : Eto.Forms.Panel
    {
        // One running claude agent inside this panel. Several can run in parallel —
        // one per UI tab — all sharing the panel's single RhinoMCP host port AND
        // the one workspace cwd (~/.corhino/workspace). Each has its own PTY and
        // its own claude session (SessionId); the session's layer prefix
        // (LayerFor) keeps parallel agents writing into disjoint Rhino layers.
        sealed class AgentTab
        {
            public required string AgentId;
            public PtySession Session = new();
            // True from spawn until StartSession finishes wiring. Counts as "alive"
            // so EnsureSessionAlive doesn't respawn a tab whose PTY is still coming up
            // (IsRunning is false until SpawnAsync returns). Set on the background
            // launch thread, read on the UI thread → volatile.
            public volatile bool Starting;
            // Output produced before the WebView is ready (and thus before the tab's
            // xterm exists). Drained on 'ready' / right after the tab is announced.
            public readonly Queue<string> Pending = new();
            // When this tab's claude last picked up the skill plugins: tab creation
            // (claude loads them at spawn) or the drawer's ⟳ (reloadSkills). Skills
            // whose SKILL.md mtime is newer show ⚠ in the drawer. Initialized here —
            // slightly BEFORE the actual spawn — so a write racing the launch can only
            // produce a false ⚠ (harmless; ⟳ converges), never a false ●.
            public DateTime SkillsLoadedAtUtc = DateTime.UtcNow;
            // The claude session id this tab runs: a fresh GUID passed via
            // --session-id on new spawns, or the resumed id (--resume). Known
            // caveats (accepted for V1): /clear inside claude rotates the id
            // silently, so this can go stale (a rename after /clear attaches to
            // the old id); and on the no-claude shell fallback the id never
            // materializes as a transcript (renaming such a tab leaves a harmless
            // orphan in session-names.json).
            public string? SessionId;
            // True → spawn with --resume SessionId (Sessions Drawer resume);
            // false → --session-id SessionId (fresh conversation).
            public bool Resume;
            // The user-given name from session-names.json, cached for label
            // computation. Null → fall back to aiTitle / agentId in the UI.
            public string? CustomName;
            // Set by UnregisterTab when the tab is removed while its launch task
            // is still running: the session's liveness claim must outlive the task
            // (see UnregisterTab), and the task's finally releases it via this
            // flag. UI thread writes, launch thread reads → volatile.
            public volatile bool ReleaseSessionOnLaunchExit;
        }

        // This session's Rhino layer prefix, derived deterministically from the
        // sessionId so a resumed session gets the SAME layer it created objects
        // in — no {sessionId → layer} index to persist. Injected per-spawn via
        // --append-system-prompt (the shared-workspace CLAUDE.md can't carry a
        // per-agent value).
        static string LayerFor(string sessionId) =>
            "s-" + sessionId.Substring(0, Math.Min(8, sessionId.Length));

        readonly WebView _webView;
        // agentId -> tab. Mutated only on the UI thread (message handlers and the
        // AsyncInvoke'd PTY callbacks), so no extra locking is needed.
        readonly Dictionary<string, AgentTab> _tabs = new();
        // Monotonic agent counter; tab N is "agent-N". Purely a UI/PTY routing
        // key — with the shared workspace it maps to no on-disk resource, so
        // collisions across panels are meaningless and a plain counter suffices.
        int _nextAgentNum = 1;
        // Session ids currently attached to a live tab, across ALL panels in this
        // process (per-doc panels share one process on macOS). Two claudes
        // resuming the same session file concurrently would interleave one
        // transcript — resumeSession must be able to see other panels' live
        // sessions, so this is static. UI-thread-only (every mutation site runs
        // on Eto's UI thread). Cross-PROCESS duplication (two Rhinos on Windows)
        // remains a documented V1 limit.
        static readonly HashSet<string> LiveSessionIds = new(StringComparer.Ordinal);
        bool _webReady;
        // Guards the reopen respawn path (EnsureSessionAlive) against the CoRhino
        // command firing again before a respawn finishes wiring.
        int _respawning;
        // Single-flight gate for background sessions scans: drawer-open and
        // tab-spawn can request scans concurrently, and overlapping scans could
        // inject their results out of order. A request landing mid-scan sets
        // _sessionsScanPending instead of starting a second scan — the in-flight
        // scan may have already read the state the request wants reflected (e.g.
        // a rename committed after the scan loaded session-names.json), so one
        // more scan runs when it finishes.
        int _sessionsScanning;
        int _sessionsScanPending;
        // The document this panel instance is keyed to. Needed to close *this*
        // panel (per-doc) when every agent has exited.
        readonly uint _documentSerialNumber;

        // The RhinoMCP host port this panel owns. Chosen once, free at construction
        // time, so each Rhino instance gets a distinct endpoint. The MCPSpawn and
        // every tab's .mcp.json use it, keeping all agents bound to *this* Rhino.
        readonly int _hostPort = Workspace.ResolveFreePort();

        // Live-update source for the Skills Drawer: watches the user's skill plugin
        // so a save lands in the list without reopening the drawer.
        FileSystemWatcher? _skillsWatcher;
        // One-shot debounce for watcher bursts (editors fire several fs events per
        // save). Re-armed on every event; fires once 300ms after the last.
        System.Threading.Timer? _skillsPushTimer;

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public CoRhinoPanel(uint documentSerialNumber)
        {
            _documentSerialNumber = documentSerialNumber;
            _webView = new WebView { BrowserContextMenuEnabled = false };
            _webView.MessageReceived += OnMessageReceived;

#if DEBUG
            try
            {
                dynamic native = _webView.ControlObject;
                native.Inspectable = true;
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] Inspector enable failed: {ex.Message}");
            }
#endif

            // Start the in-process RhinoMCP HTTP host in *this* Rhino on our own
            // port, and keep it bound to the active document. The host is per-doc,
            // so re-fire on open/new. All agents connect to it via their session
            // .mcp.json. If Rhino-MCP-Platform isn't installed there is no host to
            // start, so run the install onboarding instead (Phase 2a) and skip
            // MCPStart until the user restarts Rhino with the package present.
            Log.Write($"[CoRhino] Selected host MCP port {_hostPort}.");
            RhinoDoc.EndOpenDocument += OnDocChangedRestartHost;
            RhinoDoc.NewDocument += OnDocChangedRestartHost;
            if (RhinoMcpInstaller.IsInstalled())
            {
                EnsureHostMcpStarted();
            }
            else
            {
                Log.Write("[CoRhino] Rhino-MCP-Platform not installed; starting install onboarding.");
                RhinoMcpInstaller.RunOnboarding();
            }

            Content = _webView;
            LoadWebView();
            StartSkillsWatcher();

            // Spawn the first agent. More are added on demand when the user clicks
            // "+" in the tab bar (a 'createTab' message).
            SpawnTab();
        }

        // Register a tab: local map + the process-wide live-session set. The two
        // must move together — RegisterTab/UnregisterTab are the only mutation
        // sites.
        void RegisterTab(AgentTab tab)
        {
            _tabs[tab.AgentId] = tab;
            if (tab.SessionId != null) LiveSessionIds.Add(tab.SessionId);
        }

        void UnregisterTab(AgentTab tab)
        {
            _tabs.Remove(tab.AgentId);
            if (tab.SessionId == null) return;
            // A tab removed MID-LAUNCH keeps its session marked live until the
            // launch task quiesces: its claude may be mid-spawn (--resume already
            // attaching to the transcript), and letting another tab resume the
            // same session in that window would put two claudes on one jsonl.
            // The task's finally releases it via ReleaseSessionOnLaunchExit.
            if (tab.Starting) tab.ReleaseSessionOnLaunchExit = true;
            else LiveSessionIds.Remove(tab.SessionId);
        }

        // Allocate the next agent id, register its tab, announce it to the UI
        // (if ready), and start its claude in the background. Returns the new tab.
        AgentTab SpawnTab()
        {
            var agentId = $"agent-{_nextAgentNum++}";
            var tab = new AgentTab
            {
                AgentId = agentId,
                Starting = true,
                SessionId = Guid.NewGuid().ToString(),
            };
            RegisterTab(tab);
            WireSession(tab);

            Log.Write($"[CoRhino] Spawning tab {agentId} (session {tab.SessionId}, layer {LayerFor(tab.SessionId!)}).");
            AnnounceTab(tab);
            LaunchSessionInBackground(tab);
            RequestSessionsScan(); // live flags changed
            return tab;
        }

        // Tell the UI to create an xterm for this agent. The payload carries the
        // tab's sessionId (so the UI can send renameSession) and its initial label —
        // delivered WITH creation, not as a follow-up onTabLabel, so a resumed tab
        // never flashes "agent-N" first. No-op until the WebView is ready; the
        // 'ready' handler announces every existing tab once it boots.
        void AnnounceTab(AgentTab tab)
        {
            if (!_webReady) return;
            // customName rides along so the UI can prefill the rename input with
            // the USER's name (never the auto title — committing that on blur
            // would freeze it as a permanent custom name).
            var encoded = JsonSerializer.Serialize(new
            {
                agentId = tab.AgentId,
                sessionId = tab.SessionId,
                label = ComputeLabel(tab),
                customName = tab.CustomName,
            }, JsonOptions);
            Application.Instance?.AsyncInvoke(() =>
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabCreated({encoded})"));
        }

        // Tab label priority: user-given name > claude's auto title (from the last
        // sessions scan) > the agent id.
        string ComputeLabel(AgentTab tab)
        {
            if (!string.IsNullOrWhiteSpace(tab.CustomName)) return tab.CustomName!;
            if (tab.SessionId != null && SessionsCatalog.TryGetKnownTitle(tab.SessionId, out var title))
                return title;
            return tab.AgentId;
        }

        // Subscribe to a tab's PtySession events. The *tab instance* is captured (not
        // just its agentId) so a delayed callback from a disposed-and-reused id
        // (agent-N can recur after a reopen) is matched by reference and dropped
        // instead of mutating the new tab that now holds that id.
        void WireSession(AgentTab tab)
        {
            tab.Session.DataReceived += data => OnPtyData(tab, data);
            tab.Session.Exited += code => OnSessionExited(tab, code);
        }

        // A tab's claude exited (typically via /exit). Remove just that tab; the last
        // tab's exit closes the whole panel. Re-running the CoRhino command reopens it
        // and respawns a fresh agent via EnsureSessionAlive.
        void OnSessionExited(AgentTab tab, int code)
        {
            Log.Write($"[CoRhino] Tab {tab.AgentId} PTY exited with code {code}.");
            Application.Instance?.AsyncInvoke(() => RemoveTab(tab));
        }

        // The single teardown path for a tab, whatever triggered it (claude /exit,
        // the user's "×", or a failed start). UI-thread only. Idempotent and
        // identity-checked: if the id now maps to a *different* tab instance, this is
        // a stale call and is ignored. Disposes the PtySession (safe even if it
        // already exited), tells the UI to drop the xterm, and closes the panel once
        // no tabs remain.
        void RemoveTab(AgentTab tab)
        {
            if (!_tabs.TryGetValue(tab.AgentId, out var current) || !ReferenceEquals(current, tab))
                return;
            UnregisterTab(tab);
            try { tab.Session.Dispose(); } catch { }
            if (_webReady)
            {
                var encoded = JsonSerializer.Serialize(tab.AgentId);
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabClosed({encoded})");
            }
            if (_tabs.Count == 0)
                CloseThisPanel();
            else
                RequestSessionsScan(); // the closed tab's session becomes resumable
        }

        void CloseThisPanel()
        {
            try
            {
                // ClosePanel takes (Guid, RhinoDoc); resolve this panel's doc from
                // its serial number. Falling back to ActiveDoc (null) would close
                // the wrong instance when multiple docs each host a panel.
                var doc = RhinoDoc.FromRuntimeSerialNumber(_documentSerialNumber);
                Rhino.UI.Panels.ClosePanel(typeof(CoRhinoPanel).GUID, doc);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] ClosePanel failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        void LaunchSessionInBackground(AgentTab tab)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await StartSession(tab).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] StartSession({tab.AgentId}) unhandled: {ex.GetType().Name}: {ex.Message}");
                    Log.Write($"[CoRhino] Stack: {ex.StackTrace}");
                }
                finally
                {
                    tab.Starting = false;
                    // Deferred liveness release for a tab that was removed
                    // mid-launch (see UnregisterTab). Order matters:
                    // Starting=false first, so an UnregisterTab racing this either
                    // sees Starting==false and releases directly, or set the flag
                    // we read here — no path leaks the claim. (Double-remove is
                    // harmless; both run on the UI thread.)
                    if (tab.ReleaseSessionOnLaunchExit && tab.SessionId != null)
                        Application.Instance?.AsyncInvoke(() => LiveSessionIds.Remove(tab.SessionId));
                }
            });
        }

        /// <summary>
        /// Called by the CoRhino command after (re)opening the panel. If any agent is
        /// still alive this is a no-op (the panel just comes to front). If every agent
        /// exited (e.g. the user typed /exit in the last tab), spawn one fresh agent
        /// in the existing panel — reusing this instance's MCP host and port.
        /// </summary>
        public void EnsureSessionAlive()
        {
            // Starting counts as alive: a freshly-constructed panel calls this right
            // after the constructor's SpawnTab, while agent-1's PTY is still coming up
            // (IsRunning false until SpawnAsync returns). Without the Starting guard we
            // would clear and double-spawn agent-1.
            if (_tabs.Values.Any(t => t.Session.IsRunning || t.Starting)) return;
            // Win the right to respawn exactly once; a second command press while a
            // respawn is mid-flight is ignored.
            if (System.Threading.Interlocked.Exchange(ref _respawning, 1) != 0) return;
            try
            {
                Log.Write("[CoRhino] No agents running; respawning a fresh agent in existing panel.");

                // Drop any dead tabs and wipe the terminal so the new claude paints
                // onto a clean screen.
                foreach (var t in _tabs.Values.ToList())
                {
                    t.Session.Dispose();
                    UnregisterTab(t);
                }
                Application.Instance?.AsyncInvoke(() =>
                {
                    if (_webReady)
                        _ = _webView.ExecuteScriptAsync("window.CoRhinoPanel?.onTabsReset()");
                });

                // Restart the counter — agent ids are pure routing keys now, and
                // the fresh panel reads better starting at agent-1.
                _nextAgentNum = 1;
                SpawnTab();
            }
            finally { System.Threading.Interlocked.Exchange(ref _respawning, 0); }
        }

        void LoadWebView()
        {
            var devUrl = Environment.GetEnvironmentVariable("CORHINO_WEBUI_URL");
            if (devUrl != null)
            {
                _webView.Url = new Uri(devUrl);
                return;
            }

            var pluginDir = Path.GetDirectoryName(typeof(CoRhinoPanel).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot resolve plugin directory.");
            var indexPath = Path.Combine(pluginDir, "web", "index.html");
            if (!File.Exists(indexPath))
            {
                Log.Write($"[CoRhino] web/index.html not found at {indexPath}");
                return;
            }
            var html = File.ReadAllText(indexPath);
            // Language handshake: the shell builds its DOM synchronously at boot,
            // before it could ask the host — so the language must already be in
            // the page when the first script runs. (The dev-server path above has
            // no injection point; the UI falls back to the browser locale there.)
            html = html.Replace("<head>", $"<head><script>window.CORHINO_LANG='{L10n.Tag}'</script>");
            _webView.LoadHtml(html);
            Log.Write($"[CoRhino] LoadHtml invoked, html length={html.Length}, lang={L10n.Tag}");
        }

        async System.Threading.Tasks.Task StartSession(AgentTab tab)
        {
            Log.Write($"[CoRhino] StartSession({tab.AgentId}) entered.");
            try
            {
                var (claudePath, loginPath) = CliResolver.Resolve();
                var cwd = Workspace.EnsureWorkspace();

                var env = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(loginPath)) env["PATH"] = loginPath!;

                // Load ONLY this agent's RhinoMCP config (which points at this
                // instance's host port and carries this agent's header) and ignore
                // the user's global/project MCP settings, so claude talks to this
                // Rhino and nothing leaks in. The user's personal skills ride along
                // as a local plugin — same stance: delivered per session, nothing
                // written to ~/.claude.
                var claudeArgs = new List<string>
                {
                    "--strict-mcp-config",
                    "--mcp-config", Workspace.EnsureAgentMcpConfig(_hostPort, tab.AgentId),
                    "--plugin-dir", Workspace.EnsureUserSkillsPlugin(),
                };

                if (tab.SessionId != null)
                {
                    // Pin the session identity: fresh tabs get their pre-generated
                    // GUID (--session-id) so the tab↔transcript mapping is known
                    // without parsing anything; resumed tabs reattach to the
                    // existing id (--resume, no --fork-session — the same jsonl
                    // keeps growing).
                    claudeArgs.Add(tab.Resume ? "--resume" : "--session-id");
                    claudeArgs.Add(tab.SessionId);
                    // The parallel-agent layer, session-bound (see LayerFor). The
                    // shared workspace CLAUDE.md holds the rules; this carries the
                    // one per-session value it can't.
                    claudeArgs.Add("--append-system-prompt");
                    claudeArgs.Add(L10n.T(
                        $"このセッションの担当 Rhino レイヤは `{LayerFor(tab.SessionId)}` です。" +
                        "オブジェクトの作成・変更・削除はこのレイヤ配下のみで行ってください" +
                        "（詳細は CLAUDE.md の「並列エージェントのルール」）。",
                        $"This session's assigned Rhino layer is `{LayerFor(tab.SessionId)}`. " +
                        "Create, modify, and delete objects only under this layer " +
                        "(see the parallel-agent rules in CLAUDE.md)."));
                }

                // CoRhino's bundled cc-plugin (onboarding skills like save-rhino-skill)
                // ships next to the assembly, like web/. Skip silently if a stripped
                // install lacks it — the session works without onboarding skills.
                var bundledPlugin = Path.Combine(
                    Path.GetDirectoryName(typeof(CoRhinoPanel).Assembly.Location) ?? string.Empty,
                    "cc-plugin");
                if (Directory.Exists(bundledPlugin))
                {
                    claudeArgs.Add("--plugin-dir");
                    claudeArgs.Add(bundledPlugin);
                }

                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                if (string.IsNullOrEmpty(claudePath))
                {
                    var fallbackApp = isWindows ? "cmd.exe" : "/bin/bash";
                    var fallbackArgs = isWindows ? Array.Empty<string>() : new[] { "-l" };
                    Log.Write($"[CoRhino] `claude` CLI not found on PATH. Falling back to {fallbackApp}. PATH=" + (loginPath ?? "<none>"));
                    await tab.Session.StartAsync(fallbackApp, fallbackArgs, cwd, env).ConfigureAwait(false);
                    Log.Write($"[CoRhino] {fallbackApp} spawned in {cwd}");
                }
                else
                {
                    Log.Write($"[CoRhino] Spawning {claudePath} in {cwd}");
                    if (isWindows && !claudePath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only .exe can be launched directly by CreateProcess on Windows.
                        // .cmd / .bat / extensionless shell-shims (npm, fnm) must go through cmd.exe.
                        await tab.Session.StartAsync("cmd.exe", new[] { "/c", claudePath! }.Concat(claudeArgs).ToArray(), cwd, env).ConfigureAwait(false);
                    }
                    else
                    {
                        await tab.Session.StartAsync(claudePath!, claudeArgs, cwd, env).ConfigureAwait(false);
                    }
                    Log.Write($"[CoRhino] claude spawned in {cwd}");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] PTY start failed: {ex.GetType().Name}: {ex.Message}");
                Log.Write($"[CoRhino] Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    Log.Write($"[CoRhino] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                // The spawn failed, so this tab has no live PTY. Drop it (and close the
                // panel if it was the only one) rather than leaving a dead tab behind.
                Application.Instance?.AsyncInvoke(() => RemoveTab(tab));
            }
        }

        void EnsureHostMcpStarted()
        {
            // RhinoApp.RunScript must not run nested inside another command, but the
            // panel is often constructed *from* the CoRhino command. Defer to the
            // next Idle so the script runs at top level (otherwise the nested
            // RunScript fails and the host never comes up).
            var port = _hostPort;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                RhinoApp.Idle -= handler;
                // Use MCPSpawn (hidden ScriptRunner), NOT MCPStart. RhinoMCP deliberately
                // moved programmatic startup off the `_-MCPStart <port>` path because
                // feeding the port through the runscript engine is racy and the
                // interactive GetInteger does not reliably consume it (the leftover
                // number surfaces as "unknown command: <port>"). MCPSpawn reads the port
                // from RHINO_MCP_AUTOSTART_PORT instead. StartOrRestart semantics make
                // re-firing on each doc safe. If Rhino-MCP-Platform isn't installed the
                // command is unknown → RunScript returns false; onboarding handles install.
                try
                {
                    // Setting the env var also suppresses the plugin's own doc-open
                    // autostart (it bails when the var is set), so we own the port.
                    Environment.SetEnvironmentVariable("RHINO_MCP_AUTOSTART_PORT", port.ToString());
                    var ok = RhinoApp.RunScript("_-MCPSpawn _Enter\n", false);
                    Log.Write(ok
                        ? $"[CoRhino] MCPSpawn fired on port {port} (host MCP up)."
                        : $"[CoRhino] MCPSpawn failed (Rhino-MCP-Platform not installed?).");
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] MCPSpawn threw: {ex.GetType().Name}: {ex.Message}");
                }
            };
            RhinoApp.Idle += handler;
        }

        void OnDocChangedRestartHost(object? sender, Rhino.DocumentEventArgs e)
        {
            // The host is per-doc, but only worth restarting if the package is present.
            // While onboarding is pending (install + restart) there is nothing to start.
            if (RhinoMcpInstaller.IsInstalled())
                EnsureHostMcpStarted();
        }

        void OnMessageReceived(object? sender, WebViewMessageEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Message);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
                var type = typeEl.GetString();

                switch (type)
                {
                    case "ready":
                        _webReady = true;
                        Log.Write("[CoRhino] WebView ready.");
                        // Announce every tab that was spawned before the UI booted,
                        // then drain its buffered output onto the now-existing xterm.
                        foreach (var tab in _tabs.Values)
                        {
                            AnnounceTab(tab);
                            FlushPending(tab);
                        }
                        break;

                    case "createTab":
                        SpawnTab();
                        break;

                    case "closeTab":
                        if (root.TryGetProperty("agentId", out var closeIdEl))
                            CloseTab(closeIdEl.GetString());
                        break;

                    case "stdin":
                        if (TryGetTab(root, out var stdinTab) &&
                            root.TryGetProperty("data", out var dataEl))
                        {
                            var s = dataEl.GetString();
                            if (s != null)
                                stdinTab!.Session.Write(Encoding.UTF8.GetBytes(s));
                        }
                        break;

                    case "resize":
                        if (TryGetTab(root, out var resizeTab) &&
                            root.TryGetProperty("cols", out var colsEl) &&
                            root.TryGetProperty("rows", out var rowsEl))
                        {
                            resizeTab!.Session.Resize(colsEl.GetInt32(), rowsEl.GetInt32());
                        }
                        break;

                    case "listSkills":
                        if (TryGetTab(root, out var listTab))
                            SendSkillsList(listTab!);
                        break;

                    case "openSkill":
                        if (root.TryGetProperty("path", out var pathEl))
                            OpenSkill(pathEl.GetString());
                        break;

                    case "reloadSkills":
                        // Inject the reload into the tab's claude, advance the tab's
                        // load timestamp, and re-send the list so ⚠ dots clear. A
                        // plain UI-side stdin write couldn't advance the timestamp —
                        // that's why this is its own message.
                        if (TryGetTab(root, out var reloadTab) && reloadTab!.Session.IsRunning)
                        {
                            reloadTab.Session.Write(Encoding.UTF8.GetBytes("/plugin reload-plugins\r"));
                            reloadTab.SkillsLoadedAtUtc = DateTime.UtcNow;
                            SendSkillsList(reloadTab);
                        }
                        break;

                    case "listSessions":
                        RequestSessionsScan();
                        break;

                    case "resumeSession":
                        if (root.TryGetProperty("sessionId", out var resumeIdEl))
                            ResumeSession(resumeIdEl.GetString());
                        break;

                    case "renameSession":
                        if (root.TryGetProperty("sessionId", out var renameIdEl) &&
                            root.TryGetProperty("name", out var nameEl))
                        {
                            RenameSession(renameIdEl.GetString(), nameEl.GetString());
                        }
                        break;

                    case "debug":
                        if (root.TryGetProperty("message", out var msgEl))
                        {
                            Log.Write($"[Claude/UI] {msgEl.GetString()}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                // A malformed / version-skewed message (missing field, wrong JSON type)
                // must not tear down the message pump. Log and drop the one message.
                Log.Write($"[CoRhino] WebView message handling error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Resolve the tab an inbound message targets via its "agentId" field.
        bool TryGetTab(JsonElement root, out AgentTab? tab)
        {
            tab = null;
            if (!root.TryGetProperty("agentId", out var idEl)) return false;
            var id = idEl.GetString();
            return id != null && _tabs.TryGetValue(id, out tab);
        }

        // User closed a tab via its "×". Disposing the PtySession suppresses its
        // Exited event (so OnSessionExited will NOT fire), so we must drive the
        // teardown directly via RemoveTab — which disposes the session, drops the
        // xterm, and closes the panel if this was the last tab.
        void CloseTab(string? agentId)
        {
            if (agentId == null || !_tabs.TryGetValue(agentId, out var tab)) return;
            Log.Write($"[CoRhino] Closing tab {agentId} by user request.");
            RemoveTab(tab);
        }

        // Watch the user's skill dir and re-push the drawer list on any change.
        // Events only re-arm the 300ms one-shot timer; its callback marshals to the
        // UI thread and pushes one list per tab. The UI keeps the payload matching
        // its active tab and drops the rest — C# never tracks which tab is active,
        // and a push while the drawer is closed is simply ignored over there.
        void StartSkillsWatcher()
        {
            try
            {
                var dir = Path.Combine(Workspace.EnsureUserSkillsPlugin(), "skills");
                _skillsPushTimer = new System.Threading.Timer(_ =>
                    Application.Instance?.AsyncInvoke(PushSkillsListToAllTabs));
                _skillsWatcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                // Runs on the watcher's thread-pool thread. An in-flight event can
                // race panel Dispose; Change on the disposed timer must not throw
                // into the pool (that would take down the process).
                void Rearm()
                {
                    try { _skillsPushTimer?.Change(300, System.Threading.Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }
                FileSystemEventHandler onChange = (_, _) => Rearm();
                _skillsWatcher.Created += onChange;
                _skillsWatcher.Changed += onChange;
                _skillsWatcher.Deleted += onChange;
                _skillsWatcher.Renamed += (_, _) => Rearm();
                // A buffer overflow (many files at once) or loss of the watched dir
                // kills the watcher silently; live refresh degrades to open/tab-switch/⟳
                // only. Log it so the symptom is traceable. (No auto-recreate in V1.)
                _skillsWatcher.Error += (_, e) =>
                    Log.Write($"[CoRhino] Skills watcher error (live refresh stopped): {e.GetException()?.Message}");
                _skillsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // No watcher just means no live refresh; the drawer still updates
                // on open / tab switch / ⟳.
                Log.Write($"[CoRhino] Skills watcher unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // UI thread only (AsyncInvoke'd from the debounce timer).
        void PushSkillsListToAllTabs()
        {
            if (!_webReady) return;
            foreach (var tab in _tabs.Values)
                SendSkillsList(tab);
        }

        // Scan + push one tab's view of the skill list. The payload is a JSON object
        // literal, which is also valid JS source (System.Text.Json's default encoder
        // escapes all non-ASCII, including U+2028/U+2029), so it is inlined as the
        // onSkillsList argument.
        void SendSkillsList(AgentTab tab)
        {
            if (!_webReady) return;
            var json = SkillsCatalog.ToJson(tab.AgentId, SkillsCatalog.Scan(tab.SkillsLoadedAtUtc));
            _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onSkillsList({json})");
        }

        // Open a SKILL.md with the OS default app. Only paths the catalog returned
        // in its last scan are launchable, so the WebView cannot start arbitrary
        // files through this message.
        void OpenSkill(string? path)
        {
            if (path == null || !SkillsCatalog.IsKnownSkillPath(path))
            {
                Log.Write($"[CoRhino] openSkill rejected: {path ?? "<null>"}");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] openSkill failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Kick a background sessions scan and push the result to the drawer.
        // Scan() reads transcript files (MBs) — never on the UI thread.
        void RequestSessionsScan()
        {
            if (System.Threading.Interlocked.Exchange(ref _sessionsScanning, 1) != 0)
            {
                // A scan is in flight; make sure one more runs after it (it may
                // predate whatever change prompted this request).
                System.Threading.Interlocked.Exchange(ref _sessionsScanPending, 1);
                return;
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var list = SessionsCatalog.Scan();
                    Application.Instance?.AsyncInvoke(() => SendSessionsList(list));
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] Sessions scan task failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _sessionsScanning, 0);
                    // Release-then-check: a request that raced the release either
                    // won the fresh gate itself or left the pending flag for us.
                    if (System.Threading.Interlocked.Exchange(ref _sessionsScanPending, 0) == 1)
                        RequestSessionsScan();
                }
            });
        }

        // UI thread only: decorate the raw scan rows with tab state — the catalog
        // stays tab-blind, and _tabs must not be read from the scan thread. The
        // sessions list is tab-independent (unlike skills), so one push serves the
        // whole drawer.
        void SendSessionsList(IReadOnlyList<SessionInfo> list)
        {
            if (!_webReady) return;
            var rows = list.Select(s => new
            {
                sessionId = s.SessionId,
                title = s.Title,
                customName = s.CustomName,
                startedAt = s.StartedAtUtc.ToString("o"),
                lastActivity = s.LastActivityUtc.ToString("o"),
                // LiveSessionIds (not _tabs): a session live in ANOTHER panel of
                // this process must grey out too.
                isLive = LiveSessionIds.Contains(s.SessionId),
            });
            var json = JsonSerializer.Serialize(new { sessions = rows }, JsonOptions);
            _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onSessionsList({json})");
            // The scan may have surfaced an aiTitle for a live tab that had none —
            // refresh labels so tabs pick it up without a rename. Idempotent.
            foreach (var tab in _tabs.Values) PushTabLabel(tab);
        }

        void PushTabLabel(AgentTab tab)
        {
            if (!_webReady) return;
            var encoded = JsonSerializer.Serialize(
                new { agentId = tab.AgentId, label = ComputeLabel(tab), customName = tab.CustomName }, JsonOptions);
            _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabLabel({encoded})");
        }

        // Reopen a past session in a new tab via --resume. Only sessionIds the
        // last scan listed are accepted (allowlist — the WebView can't make us
        // resume arbitrary ids), the transcript must still exist (a claude that
        // dies instantly on a gone file would take the whole panel down with it if
        // this were the only tab), and the session must not already be live in any
        // panel of this process (two claudes on one transcript would interleave
        // it). The UI greys live rows out — these checks are defense in depth.
        void ResumeSession(string? sessionId)
        {
            if (sessionId == null || !SessionsCatalog.TryGetKnown(sessionId, out var jsonlPath))
            {
                Log.Write($"[CoRhino] resumeSession rejected (unknown id): {sessionId ?? "<null>"}");
                return;
            }
            if (LiveSessionIds.Contains(sessionId))
            {
                Log.Write($"[CoRhino] resumeSession rejected (session already live): {sessionId}");
                return;
            }
            if (!File.Exists(jsonlPath))
            {
                Log.Write($"[CoRhino] resumeSession rejected (transcript gone): {sessionId}");
                RequestSessionsScan(); // the drawer's list is stale — refresh it
                return;
            }

            var tab = new AgentTab
            {
                AgentId = $"agent-{_nextAgentNum++}",
                Starting = true,
                SessionId = sessionId,
                Resume = true,
                CustomName = SessionNames.LoadAll().TryGetValue(sessionId, out var n) ? n : null,
            };
            RegisterTab(tab);
            WireSession(tab);
            Log.Write($"[CoRhino] Resuming session {sessionId} in {tab.AgentId}.");
            AnnounceTab(tab);
            LaunchSessionInBackground(tab);
            RequestSessionsScan(); // the session just went live
        }

        // Persist a user-given session name (empty reverts to the auto title) and
        // reflect it on the live tab, if any, plus the drawer.
        void RenameSession(string? sessionId, string? name)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            SessionNames.Set(sessionId!, name);
            var normalized = string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
            foreach (var tab in _tabs.Values)
            {
                if (tab.SessionId != sessionId) continue;
                tab.CustomName = normalized;
                PushTabLabel(tab);
            }
            RequestSessionsScan();
        }

        // s arrives already decoded — PtySession owns the byte stream and holds
        // decoder state across read chunks (see ReadLoop); decoding here per-chunk
        // is exactly the bug that mangled chunk-straddling characters.
        void OnPtyData(AgentTab tab, string s)
        {
            Application.Instance?.AsyncInvoke(() =>
            {
                // Drop output from a stale session whose id has since been reused.
                if (!_tabs.TryGetValue(tab.AgentId, out var current) || !ReferenceEquals(current, tab))
                    return;
                if (!_webReady)
                {
                    tab.Pending.Enqueue(s);
                    return;
                }
                WriteToTab(tab.AgentId, s);
            });
        }

        void WriteToTab(string agentId, string s)
        {
            var encodedId = JsonSerializer.Serialize(agentId);
            var encoded = JsonSerializer.Serialize(s);
            _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onPtyData({encodedId}, {encoded})");
        }

        void FlushPending(AgentTab tab)
        {
            while (tab.Pending.Count > 0)
                WriteToTab(tab.AgentId, tab.Pending.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RhinoDoc.EndOpenDocument -= OnDocChangedRestartHost;
                RhinoDoc.NewDocument -= OnDocChangedRestartHost;
                _skillsWatcher?.Dispose();
                _skillsPushTimer?.Dispose();
                // A background scan finishing after this point must not touch the
                // disposed WebView; its AsyncInvoke'd push checks _webReady first.
                _webReady = false;
                foreach (var tab in _tabs.Values.ToList())
                {
                    tab.Session.Dispose();
                    UnregisterTab(tab);
                }
            }
            base.Dispose(disposing);
        }
    }
}
