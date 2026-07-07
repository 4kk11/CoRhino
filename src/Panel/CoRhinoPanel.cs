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
using CoRhino.Cli;
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
        // (IAgentCli.LayerTokenFor) keeps parallel agents writing into disjoint
        // Rhino layers.
        sealed class AgentTab
        {
            public required string AgentId;
            // The CLI this tab runs. Every per-CLI concern (launch args, session
            // identity, layer strategy) goes through this adapter.
            public IAgentCli Cli = AgentCliRegistry.Claude;
            public PtySession Session = new();
            // When the tab was created (UI thread), slightly BEFORE its process
            // spawns. Anchors the pending-codex drawer guard: while this tab's
            // session id is undiscovered, codex rollouts at least this fresh
            // may be *its* live session and must not be resumable/deletable.
            // Re-anchored by RelaunchCliInTab: a tab that sat in the CLI setup
            // view for a while would otherwise block every codex session newer
            // than its (stale) creation time once it finally launches.
            // UI-thread-only (all writers and the cutoff reader).
            public DateTime CreatedAtUtc = DateTime.UtcNow;
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
            // The parallel-agent Rhino layer this tab confines its writes to.
            // Resolved ONCE at tab creation via the adapter (see IAgentCli.LayerTokenFor)
            // and threaded through StartSession's SpawnContext.
            public string? LayerToken;
            // Cancels this tab's session-id discovery when the tab is removed —
            // a dead tab's poll must not claim a rollout that belongs to the
            // next codex tab (the adapter turns cancellation into burn mode).
            // Created on the UI thread at tab creation (codex only), cancelled
            // in UnregisterTab.
            public System.Threading.CancellationTokenSource? DiscoveryCts;
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
            // Non-null while the tab shows the CLI setup view instead of a live
            // PTY ("missing" | "installing" | "failed" — the onCliSetup wire
            // enum). Set when Resolve fails at spawn, cleared when a CLI (or an
            // explicit shell) actually launches. Written on launch/UI threads,
            // read by the ready handler → volatile.
            public volatile string? CliSetupPhase;
            // True while this tab's PTY runs the native INSTALLER rather than an
            // agent CLI — its exit must re-probe and relaunch, not RemoveTab
            // (see OnSessionExited).
            public volatile bool InstallerActive;
        }

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
        // Codex tabs whose session id is still undiscovered (or never resolved:
        // discovery timeout keeps the tab here for its whole life). Their live
        // session cannot be in LiveSessionIds yet, but its rollout IS on disk
        // and would otherwise list as resumable/deletable — any codex session
        // at least as fresh as the oldest pending tab is treated as live until
        // this set drains. Static + UI-thread-only, like LiveSessionIds.
        static readonly HashSet<AgentTab> PendingCodexTabs = new();
        // Codex sessions with a `codex delete` in flight (bounded ~10 s on a
        // background thread). Reserved here so the drawer row stays blocked and
        // resume is rejected until the outcome lands. UI-thread-only.
        static readonly HashSet<string> DeletingSessionIds = new(StringComparer.Ordinal);
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
            PendingCodexTabs.Remove(tab);
            try { tab.DiscoveryCts?.Cancel(); } catch (ObjectDisposedException) { }
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
        // (if ready), and start its agent CLI in the background. Returns the new tab.
        AgentTab SpawnTab(AgentCliKind kind = AgentCliKind.Claude)
        {
            var cli = AgentCliRegistry.For(kind);
            var agentId = $"agent-{_nextAgentNum++}";
            var tab = new AgentTab
            {
                AgentId = agentId,
                Cli = cli,
                Starting = true,
                // Claude pins its id up front (--session-id); codex generates its
                // own — discovery fills SessionId in after launch.
                SessionId = kind == AgentCliKind.Claude ? Guid.NewGuid().ToString() : null,
            };
            tab.LayerToken = cli.LayerTokenFor(tab.SessionId);
            if (tab.SessionId == null)
            {
                tab.DiscoveryCts = new System.Threading.CancellationTokenSource();
                PendingCodexTabs.Add(tab);
            }
            RegisterTab(tab);
            WireSession(tab);

            Log.Write($"[CoRhino] Spawning tab {agentId} ({cli.BinaryName}, session {tab.SessionId ?? "<pending>"}, layer {tab.LayerToken}).");
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
                cli = CliId(tab),
            }, JsonOptions);
            Application.Instance?.AsyncInvoke(() =>
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabCreated({encoded})"));
        }

        // The wire id of a CLI ("claude" / "codex") — the protocol's enum,
        // shared by onTabCreated / onCliAvailability / onSessionsList / createTab.
        static string CliId(AgentTab tab) => CliId(tab.Cli.Kind);
        static string CliId(AgentCliKind kind) => kind == AgentCliKind.Codex ? "codex" : "claude";

        // Tab label priority: user-given name > the CLI's auto title (from the last
        // sessions scan) > the agent id.
        string ComputeLabel(AgentTab tab)
        {
            if (!string.IsNullOrWhiteSpace(tab.CustomName)) return tab.CustomName!;
            if (tab.SessionId != null)
            {
                var found = tab.Cli.Kind == AgentCliKind.Codex
                    ? CodexSessionsCatalog.TryGetKnownTitle(tab.SessionId, out var title)
                    : SessionsCatalog.TryGetKnownTitle(tab.SessionId, out title);
                if (found) return title;
            }
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
        // and respawns a fresh agent via EnsureSessionAlive. Exception: a native
        // INSTALLER run finishing is not the tab dying — re-probe and relaunch.
        void OnSessionExited(AgentTab tab, int code)
        {
            Log.Write($"[CoRhino] Tab {tab.AgentId} PTY exited with code {code}.");
            Application.Instance?.AsyncInvoke(() =>
            {
                if (tab.InstallerActive)
                {
                    tab.InstallerActive = false;
                    HandleInstallerExit(tab, code);
                    return;
                }
                RemoveTab(tab);
            });
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

        // ---- CLI setup view (missing-CLI onboarding) --------------------------
        // See docs/plans/cli-onboarding-design.md. A tab whose CLI didn't resolve
        // shows an HTML setup view instead of a silent shell; its install button
        // streams the official native installer through the tab's own PTY, and a
        // successful probe relaunches the CLI in place.

        // Tell the UI this tab's setup state ("missing"/"installing"/"failed").
        // Safe to call from any thread; no-op when the tab isn't in setup. The
        // phase is (re)read INSIDE the invoke, after an identity check — a
        // reopen-respawn can retire this tab and hand its agentId to a fresh
        // one, and a stale queued push must not paint an overlay onto it.
        void PushCliSetup(AgentTab tab)
        {
            Application.Instance?.AsyncInvoke(() =>
            {
                if (!_webReady) return;
                if (!_tabs.TryGetValue(tab.AgentId, out var current) || !ReferenceEquals(current, tab)) return;
                var phase = tab.CliSetupPhase;
                if (phase == null) return;
                var encoded = JsonSerializer.Serialize(new
                {
                    agentId = tab.AgentId,
                    phase,
                    cli = CliId(tab),
                    command = tab.Cli.NativeInstallCommand,
                }, JsonOptions);
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onCliSetup({encoded})");
            });
        }

        // PtySession is one-shot (StartAsync throws on reuse, exit is sticky), so
        // every relaunch in an existing tab needs a fresh instance. The UI re-sends
        // the pane geometry when the overlay clears — the new PTY would otherwise
        // sit on the 80x24 default. UI thread (field swap must not race handlers).
        void ReplaceSession(AgentTab tab)
        {
            try { tab.Session.Dispose(); } catch { }
            tab.Session = new PtySession();
            WireSession(tab);
        }

        // The setup view's install button: run the CLI's official native installer
        // in this tab's PTY so the user watches it live (and can Ctrl-C it). Its
        // exit lands in OnSessionExited → HandleInstallerExit. UI thread.
        void BeginCliInstall(AgentTab tab)
        {
            if (tab.Starting || tab.Session.IsRunning || tab.InstallerActive) return;
            if (tab.CliSetupPhase == null) return;
            tab.InstallerActive = true;
            tab.CliSetupPhase = "installing";
            PushCliSetup(tab); // UI drops the overlay and re-sends the pane size
            ReplaceSession(tab); // the previous session may be a finished failed run
            tab.Starting = true;
            var command = tab.Cli.NativeInstallCommand;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var cwd = Workspace.EnsureWorkspace();
                    // Same wrappers the command is documented for: a login shell
                    // (proxy/env vars) on unix, powershell on Windows.
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        await tab.Session.StartAsync("powershell.exe", new[] { "-ExecutionPolicy", "Bypass", "-Command", command }, cwd).ConfigureAwait(false);
                    else
                        await tab.Session.StartAsync("/bin/bash", new[] { "-lc", command }, cwd).ConfigureAwait(false);
                    Log.Write($"[CoRhino] Native installer for {tab.Cli.BinaryName} started in {tab.AgentId}.");
                }
                catch (Exception ex)
                {
                    Log.User($"[CoRhino] Installer start failed: {ex.GetType().Name}: {ex.Message}");
                    Application.Instance?.AsyncInvoke(() =>
                    {
                        tab.InstallerActive = false;
                        tab.CliSetupPhase = "failed";
                        PushCliSetup(tab);
                    });
                }
                finally { tab.Starting = false; }
            });
        }

        // Installer PTY exited: the probe result (not the exit code) decides —
        // "already installed" exits 0 without changing anything, and a user may
        // have Ctrl-C'd after the binary landed. UI thread.
        void HandleInstallerExit(AgentTab tab, int code)
        {
            if (!_tabs.TryGetValue(tab.AgentId, out var current) || !ReferenceEquals(current, tab))
                return; // tab was closed while the installer ran
            Log.Write($"[CoRhino] Installer for {tab.Cli.BinaryName} exited with code {code}; probing.");
            System.Threading.Tasks.Task.Run(() =>
            {
                var found = CliResolver.Resolve(tab.Cli.BinaryName).cliPath != null;
                Application.Instance?.AsyncInvoke(() =>
                {
                    if (!_tabs.TryGetValue(tab.AgentId, out var cur) || !ReferenceEquals(cur, tab)) return;
                    if (tab.Starting || tab.Session.IsRunning || tab.InstallerActive) return;
                    if (found)
                    {
                        RelaunchCliInTab(tab);
                    }
                    else
                    {
                        // Bottom banner, not the full overlay — the installer's
                        // output stays readable for diagnosis.
                        tab.CliSetupPhase = "failed";
                        PushCliSetup(tab);
                    }
                });
            });
        }

        // The setup view's re-check button (manual-install path). UI thread.
        void RecheckCli(AgentTab tab)
        {
            if (tab.Starting || tab.Session.IsRunning || tab.InstallerActive) return;
            if (tab.CliSetupPhase == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                var found = CliResolver.Resolve(tab.Cli.BinaryName).cliPath != null;
                Application.Instance?.AsyncInvoke(() =>
                {
                    if (!_tabs.TryGetValue(tab.AgentId, out var cur) || !ReferenceEquals(cur, tab)) return;
                    if (tab.Starting || tab.Session.IsRunning || tab.InstallerActive) return;
                    if (found) RelaunchCliInTab(tab);
                    else
                    {
                        tab.CliSetupPhase = "failed";
                        PushCliSetup(tab);
                    }
                });
            });
        }

        // The CLI probes as present now — start it in this same tab (fresh PTY,
        // wiped screen) and let the + menu learn it exists. UI thread.
        void RelaunchCliInTab(AgentTab tab)
        {
            Log.Write($"[CoRhino] {tab.Cli.BinaryName} now resolves; launching in {tab.AgentId}.");
            tab.CliSetupPhase = null;
            // A fresh codex spawn is imminent again — rejoin the pending-codex
            // guard (StartSession's missing branch dropped it), re-anchored to
            // now so only rollouts from THIS launch window get blocked.
            if (tab.Cli.Kind == AgentCliKind.Codex && tab.SessionId == null)
            {
                tab.CreatedAtUtc = DateTime.UtcNow;
                PendingCodexTabs.Add(tab);
            }
            ReplaceSession(tab);
            tab.Starting = true;
            if (_webReady)
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onSessionRestart({JsonSerializer.Serialize(tab.AgentId)})");
            LaunchSessionInBackground(tab);
            PushCliAvailability(); // un-grey the + menu without a panel reload
        }

        // The setup view's explicit "open a shell" escape hatch — the old silent
        // fallback, now opt-in. A plain tab: its exit removes it as usual. UI thread.
        void OpenShellInTab(AgentTab tab)
        {
            if (tab.Starting || tab.Session.IsRunning || tab.InstallerActive) return;
            if (tab.CliSetupPhase == null) return;
            tab.CliSetupPhase = null;
            ReplaceSession(tab);
            tab.Starting = true;
            if (_webReady)
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onSessionRestart({JsonSerializer.Serialize(tab.AgentId)})");
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var cwd = Workspace.EnsureWorkspace();
                    var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                    var app = isWindows ? "cmd.exe" : "/bin/bash";
                    var shellArgs = isWindows ? Array.Empty<string>() : new[] { "-l" };
                    await tab.Session.StartAsync(app, shellArgs, cwd).ConfigureAwait(false);
                    Log.Write($"[CoRhino] {app} spawned in {cwd} ({tab.AgentId}).");
                }
                catch (Exception ex)
                {
                    Log.User($"[CoRhino] Shell start failed: {ex.GetType().Name}: {ex.Message}");
                    Application.Instance?.AsyncInvoke(() => RemoveTab(tab));
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
                Log.User($"[CoRhino] web/index.html not found at {indexPath}");
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
                var cli = tab.Cli;
                var (cliPath, loginPath) = CliResolver.Resolve(cli.BinaryName);
                var cwd = Workspace.EnsureWorkspace();

                var env = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(loginPath)) env["PATH"] = loginPath!;

                // Everything CLI-specific — MCP isolation, session identity,
                // layer-instruction injection — is the adapter's job; this method
                // only owns the CLI-independent PTY plumbing.
                var ctx = new SpawnContext(
                    tab.AgentId, _hostPort, cwd,
                    tab.SessionId, tab.Resume,
                    tab.LayerToken,
                    DateTime.UtcNow);
                var args = cli.BuildArgs(ctx);

                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                if (string.IsNullOrEmpty(cliPath))
                {
                    // No silent shell fallback: an unexplained bash tab reads as
                    // a broken panel. Show the setup view instead — install /
                    // re-check / (explicit) shell live there.
                    Log.User($"[CoRhino] `{cli.BinaryName}` CLI not found; showing the setup view.");
                    Log.Write("[CoRhino] PATH=" + (loginPath ?? "<none>"));
                    tab.CliSetupPhase = "missing";
                    // No codex process is coming (yet), so this tab must not hold
                    // the pending-codex guard — it would block resume/delete of
                    // unrelated codex sessions for as long as the setup view sits
                    // there. RelaunchCliInTab re-registers before a real launch.
                    Application.Instance?.AsyncInvoke(() => PendingCodexTabs.Remove(tab));
                    PushCliSetup(tab);
                }
                else
                {
                    // A CLI resolved through the well-known-dir fallback
                    // (~/.local/bin — see CliResolver) may not be on the login
                    // PATH yet (the native installers write their PATH line to a
                    // profile the bash -lc probe doesn't read). Prepend its dir
                    // so the CLI's own subprocesses find it too.
                    var binDir = Path.GetDirectoryName(cliPath);
                    if (!string.IsNullOrEmpty(binDir))
                    {
                        var basePath = env.TryGetValue("PATH", out var p)
                            ? p
                            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                        if (!basePath.Split(Path.PathSeparator).Contains(binDir))
                            env["PATH"] = binDir + Path.PathSeparator + basePath;
                    }

                    // Session-id discovery matches "a fresh rollout in our cwd" —
                    // with all codex tabs sharing one cwd, two in-flight launches
                    // would be indistinguishable and could swap ids. The gate
                    // serializes [launch → rollout claimed] process-wide so at
                    // most one unclaimed matching rollout exists at a time.
                    // Discovery lands in seconds typically; a second + click just
                    // starts a beat later.
                    var holdsDiscoveryGate = tab.SessionId == null;
                    if (holdsDiscoveryGate)
                        await CodexSpawnGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        Log.Write($"[CoRhino] Spawning {cliPath} in {cwd}");
                        if (isWindows && !cliPath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            // Only .exe can be launched directly by CreateProcess on Windows.
                            // .cmd / .bat / extensionless shell-shims (npm, fnm) must go through cmd.exe.
                            await tab.Session.StartAsync("cmd.exe", new[] { "/c", cliPath! }.Concat(args).ToArray(), cwd, env).ConfigureAwait(false);
                        }
                        else
                        {
                            await tab.Session.StartAsync(cliPath!, args, cwd, env).ConfigureAwait(false);
                        }
                        Log.Write($"[CoRhino] {cli.BinaryName} spawned in {cwd}");
                        tab.CliSetupPhase = null; // a real CLI is running now

                        // Codex generates its own session id — recover it from the
                        // rollout store so the tab becomes resumable and its layer
                        // pairing persists. Fire-and-forget: Starting must clear as
                        // soon as the PTY is up, not after a (worst-case 15 s) poll.
                        // The discovery task takes over the gate and releases it.
                        if (holdsDiscoveryGate)
                        {
                            _ = System.Threading.Tasks.Task.Run(() => DiscoverSessionId(tab, ctx));
                            holdsDiscoveryGate = false; // ownership handed off
                        }
                    }
                    finally
                    {
                        if (holdsDiscoveryGate) CodexSpawnGate.Release(); // spawn threw before handoff
                    }
                }
            }
            catch (Exception ex)
            {
                Log.User($"[CoRhino] PTY start failed: {ex.GetType().Name}: {ex.Message}");
                Log.Write($"[CoRhino] Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    Log.Write($"[CoRhino] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                // The spawn failed, so this tab has no live PTY. Drop it (and close the
                // panel if it was the only one) rather than leaving a dead tab behind.
                Application.Instance?.AsyncInvoke(() => RemoveTab(tab));
            }
        }

        // Serializes codex [launch → session-id claimed] process-wide (static:
        // macOS runs every panel in one process and they all share the
        // workspace cwd). See the spawn site for why.
        static readonly System.Threading.SemaphoreSlim CodexSpawnGate = new(1, 1);

        // Background side of codex session-id discovery; hands the result to the
        // UI thread. The TAB INSTANCE is captured — a stale discovery (tab since
        // removed / id reused) must not touch the tab that now holds the id.
        // Owns the spawn gate (handed over by StartSession) and ALWAYS releases it.
        async System.Threading.Tasks.Task DiscoverSessionId(AgentTab tab, SpawnContext ctx)
        {
            try
            {
                var ct = tab.DiscoveryCts?.Token ?? System.Threading.CancellationToken.None;
                // Cancellation is handled INSIDE the adapter (burn mode — the
                // rollout must still be claimed), so a cancelled discovery
                // returns null rather than throwing.
                var sessionId = await tab.Cli.DiscoverSessionIdAsync(ctx, ct).ConfigureAwait(false);
                if (sessionId == null) return; // logged by the adapter; tab stays usable
                Application.Instance?.AsyncInvoke(() => OnSessionIdDiscovered(tab, sessionId));
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] Session id discovery failed ({tab.AgentId}): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CodexSpawnGate.Release();
            }
        }

        // UI thread only. Wires a discovered codex session id into the tab:
        // liveness (resume guard), the persisted layer pairing, and the UI's
        // copy of the id (rename needs it).
        void OnSessionIdDiscovered(AgentTab tab, string sessionId)
        {
            // The session exists on disk with this layer regardless of whether
            // the tab survived — persist the pairing so a later resume restores
            // the same layer.
            if (tab.LayerToken != null)
                SessionLayers.Set(sessionId, tab.LayerToken);

            if (!_tabs.TryGetValue(tab.AgentId, out var current) || !ReferenceEquals(current, tab))
                return; // tab gone; nothing live to mark
            tab.SessionId = sessionId;
            PendingCodexTabs.Remove(tab); // identity known — the precise guard takes over
            LiveSessionIds.Add(sessionId);
            tab.CustomName = SessionNames.LoadAll().TryGetValue(sessionId, out var n) ? n : null;
            if (_webReady)
            {
                var encoded = JsonSerializer.Serialize(
                    new { agentId = tab.AgentId, sessionId }, JsonOptions);
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onTabSession({encoded})");
            }
            PushTabLabel(tab);
        }

        // Probe which agent CLIs exist on PATH and tell the UI (the + menu greys
        // out missing ones). Once per 'ready' — the probe forks a login shell
        // per CLI, so it must not run per menu open; a CLI installed later is
        // picked up when the panel reloads (spawns themselves always re-resolve).
        void PushCliAvailability()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var claude = CliResolver.Resolve(AgentCliRegistry.Claude.BinaryName).cliPath != null;
                var codex = CliResolver.Resolve(AgentCliRegistry.Codex.BinaryName).cliPath != null;
                Application.Instance?.AsyncInvoke(() =>
                {
                    if (!_webReady) return;
                    var encoded = JsonSerializer.Serialize(new { claude, codex }, JsonOptions);
                    _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onCliAvailability({encoded})");
                });
            });
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
                    if (ok)
                        Log.Write($"[CoRhino] MCPSpawn fired on port {port} (host MCP up).");
                    else
                        Log.User("[CoRhino] MCPSpawn failed (Rhino-MCP-Platform not installed?).");
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
                            // agent-1 typically resolves (or fails to) before the
                            // UI boots — replay its setup state onto the fresh DOM.
                            PushCliSetup(tab);
                        }
                        PushCliAvailability();
                        break;

                    case "createTab":
                        // Optional cli field ("claude" | "codex"); absent → claude.
                        SpawnTab(
                            root.TryGetProperty("cli", out var cliEl) && cliEl.GetString() == "codex"
                                ? AgentCliKind.Codex
                                : AgentCliKind.Claude);
                        break;

                    case "closeTab":
                        if (root.TryGetProperty("agentId", out var closeIdEl))
                            CloseTab(closeIdEl.GetString());
                        break;

                    // Setup-view actions (all no-ops unless the tab is in setup).
                    case "installCli":
                        if (TryGetTab(root, out var installTab)) BeginCliInstall(installTab!);
                        break;

                    case "recheckCli":
                        if (TryGetTab(root, out var recheckTab)) RecheckCli(recheckTab!);
                        break;

                    case "openShellInTab":
                        if (TryGetTab(root, out var shellTab)) OpenShellInTab(shellTab!);
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
                        if (TryGetTab(root, out var listTab) && listTab!.Cli.SupportsSkillsDrawer)
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
                        // Only CLIs with an in-session reload path (claude). The UI
                        // hides ⟳ for the rest — this guard is defense in depth.
                        if (TryGetTab(root, out var reloadTab) && reloadTab!.Cli.SupportsSkillsReload &&
                            reloadTab.Session.IsRunning)
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

                    case "deleteSession":
                        if (root.TryGetProperty("sessionId", out var deleteIdEl))
                            DeleteSession(deleteIdEl.GetString());
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
            IReadOnlyList<SkillInfo> skills = SkillsCatalog.Scan(tab.SkillsLoadedAtUtc);
            // The drawer shows what THIS tab's session actually has: codex mounts
            // both skill sources via workspace symlinks, and each source is
            // listed only while its link really is ours and in place (creation
            // can fail, and the user may replace an entry).
            if (tab.Cli.LoadsSkillsViaWorkspaceMount)
            {
                var userOk = Workspace.CodexUserSkillsLinkActive();
                var bundledOk = Workspace.CodexBundledSkillsLinkActive();
                skills = skills.Where(s => s.Source == "user" ? userOk : bundledOk).ToList();
            }
            var json = SkillsCatalog.ToJson(tab.AgentId, skills);
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
                    // Both CLIs' catalogs, one merged list — the drawer shows a
                    // single timeline with per-row CLI badges.
                    var list = SessionsCatalog.Scan()
                        .Concat(CodexSessionsCatalog.Scan())
                        .OrderByDescending(s => s.LastActivityUtc)
                        .ToList();
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
                cli = CliId(s.Cli),
                // Blocked = attached to a live tab in ANY panel of this process,
                // possibly-live (pending codex id), or mid-delete — see
                // IsSessionBlocked.
                isLive = IsSessionBlocked(s.SessionId, s.Cli, s.LastActivityUtc),
            });
            var json = JsonSerializer.Serialize(new { sessions = rows }, JsonOptions);
            _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onSessionsList({json})");
            // The scan may have surfaced an aiTitle for a live tab that had none —
            // refresh labels so tabs pick it up without a rename. Idempotent.
            foreach (var tab in _tabs.Values) PushTabLabel(tab);
        }

        // UI thread only. A codex session at least as fresh as the oldest
        // pending (id-undiscovered) codex tab might BE that tab's live session —
        // nothing can prove otherwise until the id lands, so err on blocking.
        static DateTime? PendingCodexCutoffUtc() =>
            PendingCodexTabs.Count == 0 ? null : PendingCodexTabs.Min(t => t.CreatedAtUtc).AddSeconds(-5);

        // UI thread only: the single definition of "hands off this session",
        // shared by the drawer decoration and the resume/delete guards.
        static bool IsSessionBlocked(string sessionId, AgentCliKind cli, DateTime lastActivityUtc)
        {
            if (LiveSessionIds.Contains(sessionId)) return true;
            if (DeletingSessionIds.Contains(sessionId)) return true;
            if (cli == AgentCliKind.Codex)
            {
                var cutoff = PendingCodexCutoffUtc();
                if (cutoff != null && lastActivityUtc >= cutoff) return true;
            }
            return false;
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
            // Whichever CLI's catalog listed the id owns the resume; ids are
            // UUIDs on both sides, so the two allowlists cannot collide.
            IAgentCli cli;
            string transcriptPath;
            if (sessionId != null && SessionsCatalog.TryGetKnown(sessionId, out var jsonlPath))
            {
                cli = AgentCliRegistry.Claude;
                transcriptPath = jsonlPath;
            }
            else if (sessionId != null && CodexSessionsCatalog.TryGetKnown(sessionId, out var rolloutPath))
            {
                cli = AgentCliRegistry.Codex;
                transcriptPath = rolloutPath;
            }
            else
            {
                Log.Write($"[CoRhino] resumeSession rejected (unknown id): {sessionId ?? "<null>"}");
                return;
            }
            if (!File.Exists(transcriptPath))
            {
                Log.Write($"[CoRhino] resumeSession rejected (transcript gone): {sessionId}");
                RequestSessionsScan(); // the drawer's list is stale — refresh it
                return;
            }
            if (IsSessionBlocked(sessionId!, cli.Kind, File.GetLastWriteTimeUtc(transcriptPath)))
            {
                Log.Write($"[CoRhino] resumeSession rejected (session live/pending/deleting): {sessionId}");
                return;
            }

            var tab = new AgentTab
            {
                AgentId = $"agent-{_nextAgentNum++}",
                Cli = cli,
                Starting = true,
                SessionId = sessionId,
                Resume = true,
                CustomName = SessionNames.LoadAll().TryGetValue(sessionId!, out var n) ? n : null,
            };
            tab.LayerToken = tab.Cli.LayerTokenFor(sessionId!);
            RegisterTab(tab);
            WireSession(tab);
            Log.Write($"[CoRhino] Resuming {cli.BinaryName} session {sessionId} in {tab.AgentId}.");
            AnnounceTab(tab);
            LaunchSessionInBackground(tab);
            RequestSessionsScan(); // the session just went live
        }

        // Delete a past session's transcript (the jsonl plus its subagents
        // sidecar dir) and its custom name. Destructive and user-initiated — the
        // drawer's 🗑 double-click confirm is the consent step; here we only
        // enforce safety: allowlisted ids only (the WebView can't make us delete
        // arbitrary files) and never a session that is currently attached to a
        // tab in this process.
        void DeleteSession(string? sessionId)
        {
            if (sessionId == null)
            {
                Log.Write("[CoRhino] deleteSession rejected (unknown id): <null>");
                return;
            }
            if (SessionsCatalog.TryGetKnown(sessionId, out var jsonlPath))
            {
                if (IsSessionBlocked(sessionId, AgentCliKind.Claude, DateTime.MinValue))
                {
                    Log.Write($"[CoRhino] deleteSession rejected (session live): {sessionId}");
                    return;
                }
                // Claude stores plain files — delete them directly.
                try
                {
                    if (File.Exists(jsonlPath)) File.Delete(jsonlPath);
                    // Subagent transcripts live in a sibling dir named after the session.
                    var subagentsDir = Path.Combine(Path.GetDirectoryName(jsonlPath) ?? string.Empty, sessionId);
                    if (Directory.Exists(subagentsDir)) Directory.Delete(subagentsDir, recursive: true);
                    SessionNames.Set(sessionId, null); // drop its custom name too
                    Log.Write($"[CoRhino] Deleted session {sessionId}.");
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] deleteSession failed ({sessionId}): {ex.GetType().Name}: {ex.Message}");
                }
                RequestSessionsScan();
            }
            else if (CodexSessionsCatalog.TryGetKnown(sessionId, out var rolloutPath))
            {
                var mtime = File.Exists(rolloutPath) ? File.GetLastWriteTimeUtc(rolloutPath) : DateTime.MinValue;
                if (IsSessionBlocked(sessionId, AgentCliKind.Codex, mtime))
                {
                    Log.Write($"[CoRhino] deleteSession rejected (session live/pending/deleting): {sessionId}");
                    return;
                }
                // Codex keeps an index and archive around its rollouts — deletion
                // goes through `codex delete <id>` so that bookkeeping stays
                // consistent. The CLI call blocks (bounded) → background thread;
                // the id is reserved in DeletingSessionIds until the outcome lands
                // (blocks resume for the window), and on failure the row stays
                // listed (nothing silently vanishes).
                DeletingSessionIds.Add(sessionId);
                RequestSessionsScan(); // re-render now so the row blocks immediately
                System.Threading.Tasks.Task.Run(() =>
                {
                    var deleted = CodexSessionsCatalog.Delete(sessionId);
                    if (deleted)
                        SessionNames.Set(sessionId, null); // drop its custom name too
                    Application.Instance?.AsyncInvoke(() =>
                    {
                        DeletingSessionIds.Remove(sessionId);
                        RequestSessionsScan();
                    });
                });
            }
            else
            {
                Log.Write($"[CoRhino] deleteSession rejected (unknown id): {sessionId}");
            }
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
