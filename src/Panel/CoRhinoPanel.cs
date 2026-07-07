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
        // one per UI tab — all sharing the panel's single RhinoMCP host port. Each
        // has its own PTY, its own session dir/cwd, and a private layer prefix so
        // parallel agents write into disjoint Rhino layers.
        sealed class AgentTab
        {
            public required string AgentId;
            public required string LayerPrefix;
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
        }

        readonly WebView _webView;
        // agentId -> tab. Mutated only on the UI thread (message handlers and the
        // AsyncInvoke'd PTY callbacks), so no extra locking is needed.
        readonly Dictionary<string, AgentTab> _tabs = new();
        // Monotonic agent counter; tab N is "agent-N". Never reused within a panel
        // lifetime so two tabs never collide on a session dir or layer.
        int _nextAgentNum = 1;
        bool _webReady;
        // Guards the reopen respawn path (EnsureSessionAlive) against the CoRhino
        // command firing again before a respawn finishes wiring.
        int _respawning;
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

        // Allocate the next agent id, register its tab, announce it to the UI (if
        // ready), and start its claude in the background. Returns the new tab.
        AgentTab SpawnTab()
        {
            var agentId = $"agent-{_nextAgentNum++}";
            var tab = new AgentTab { AgentId = agentId, LayerPrefix = agentId, Starting = true };
            _tabs[agentId] = tab;
            WireSession(tab);

            Log.Write($"[CoRhino] Spawning tab {agentId} (layer {tab.LayerPrefix}).");
            AnnounceTab(agentId);
            LaunchSessionInBackground(tab);
            return tab;
        }

        // Tell the UI to create an xterm for this agent. No-op until the WebView is
        // ready; the 'ready' handler announces every existing tab once it boots.
        void AnnounceTab(string agentId)
        {
            if (!_webReady) return;
            var encoded = JsonSerializer.Serialize(agentId);
            Application.Instance?.AsyncInvoke(() =>
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabCreated({encoded})"));
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
            _tabs.Remove(tab.AgentId);
            try { tab.Session.Dispose(); } catch { }
            if (_webReady)
            {
                var encoded = JsonSerializer.Serialize(tab.AgentId);
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabClosed({encoded})");
            }
            if (_tabs.Count == 0)
                CloseThisPanel();
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
                finally { tab.Starting = false; }
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
                foreach (var t in _tabs.Values) t.Session.Dispose();
                _tabs.Clear();
                Application.Instance?.AsyncInvoke(() =>
                {
                    if (_webReady)
                        _ = _webView.ExecuteScriptAsync("window.CoRhinoPanel?.onTabsReset()");
                });

                // Reuse agent-1 so the reopened session keeps its layer prefix, its
                // seeded CLAUDE.md, and its claude history (cwd-keyed).
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
            _webView.LoadHtml(html);
            Log.Write($"[CoRhino] LoadHtml invoked, html length={html.Length}");
        }

        async System.Threading.Tasks.Task StartSession(AgentTab tab)
        {
            Log.Write($"[CoRhino] StartSession({tab.AgentId}) entered.");
            try
            {
                var (claudePath, loginPath) = CliResolver.Resolve();
                var cwd = Workspace.EnsureSession(_hostPort, tab.AgentId, tab.LayerPrefix);

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
                    "--mcp-config", Workspace.McpConfigPath(_hostPort, tab.AgentId),
                    "--plugin-dir", Workspace.EnsureUserSkillsPlugin(),
                };

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
                            AnnounceTab(tab.AgentId);
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

        void OnPtyData(AgentTab tab, byte[] data)
        {
            var s = Encoding.UTF8.GetString(data);
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
                foreach (var tab in _tabs.Values) tab.Session.Dispose();
                _tabs.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
