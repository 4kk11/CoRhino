using System;
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
        readonly WebView _webView;
        PtySession _session = new();
        readonly System.Collections.Generic.Queue<string> _pendingOutput = new();
        bool _webReady;
        // Guards EnsureSessionAlive against re-entrancy: the CoRhino command can fire
        // again before a respawn finishes wiring, and we must not spawn two claudes.
        int _starting;
        // The document this panel instance is keyed to. Needed to close *this*
        // panel (per-doc) when the claude session ends.
        readonly uint _documentSerialNumber;

        // The RhinoMCP host port this panel owns. Chosen once, free at construction
        // time, so each Rhino instance gets a distinct endpoint. Both MCPStart and
        // the generated .mcp.json use it, keeping claude bound to *this* Rhino.
        readonly int _hostPort = Workspace.ResolveFreePort();

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

            WireSession(_session);

            // Start the in-process RhinoMCP HTTP host in *this* Rhino on our own
            // port, and keep it bound to the active document. The host is per-doc,
            // so re-fire on open/new. claude connects to it via the session .mcp.json.
            // If Rhino-MCP-Platform isn't installed there is no host to start, so run
            // the install onboarding instead (Phase 2a) and skip MCPStart until the
            // user restarts Rhino with the package present.
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

            Log.Write("[CoRhino] Scheduling StartSession on background thread...");
            // Claim the starting flag so an early CoRhino re-press can't respawn a
            // second claude before the initial spawn has finished.
            System.Threading.Interlocked.Exchange(ref _starting, 1);
            LaunchSessionInBackground();
        }

        // Subscribe to a (fresh) PtySession's events. Called for the initial session
        // and for every respawn so the panel always tracks the live session.
        void WireSession(PtySession session)
        {
            session.DataReceived += OnPtyData;
            session.Exited += OnSessionExited;
        }

        // claude exited (typically via /exit). The PtySession has already torn its
        // connection down, so IsRunning is now false. Close this panel; re-running
        // the CoRhino command reopens it and respawns via EnsureSessionAlive (the
        // cached instance keeps its MCP host, port, and session dir).
        void OnSessionExited(int code)
        {
            Log.Write($"[CoRhino] PTY exited with code {code}; closing panel.");
            Application.Instance?.AsyncInvoke(() =>
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
            });
        }

        void LaunchSessionInBackground()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await StartSession().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] StartSession unhandled: {ex.GetType().Name}: {ex.Message}");
                    Log.Write($"[CoRhino] Stack: {ex.StackTrace}");
                }
                finally { System.Threading.Interlocked.Exchange(ref _starting, 0); }
            });
        }

        /// <summary>
        /// Called by the CoRhino command after (re)opening the panel. If the claude
        /// session is still alive this is a no-op (the panel just comes to front).
        /// If it exited (e.g. the user typed /exit), spawn a fresh session in the
        /// existing panel — reusing this instance's MCP host, port, and session dir.
        /// </summary>
        public void EnsureSessionAlive()
        {
            if (_session.IsRunning) return;
            // Win the right to restart exactly once; a second command press while a
            // respawn is mid-flight is ignored.
            if (System.Threading.Interlocked.Exchange(ref _starting, 1) != 0) return;

            Log.Write("[CoRhino] Session not running; respawning claude in existing panel.");
            var old = _session;
            old.Dispose();

            var session = new PtySession();
            WireSession(session);
            _session = session;

            // Drop any output buffered from the dead session and clear the terminal
            // so the new claude paints onto a clean screen.
            _pendingOutput.Clear();
            Application.Instance?.AsyncInvoke(() =>
            {
                if (_webReady)
                    _ = _webView.ExecuteScriptAsync("window.CoRhinoPanel?.onSessionRestart()");
            });

            LaunchSessionInBackground();
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

        async System.Threading.Tasks.Task StartSession()
        {
            Log.Write("[CoRhino] StartSession() entered.");
            try
            {
                var (claudePath, loginPath) = CliResolver.Resolve();
                var cwd = Workspace.EnsureSession(_hostPort);

                var env = new System.Collections.Generic.Dictionary<string, string>();
                if (!string.IsNullOrEmpty(loginPath)) env["PATH"] = loginPath!;

                // Load ONLY this session's RhinoMCP config (which points at this
                // instance's host port) and ignore the user's global/project MCP
                // settings, so claude talks to this Rhino and nothing leaks in.
                var claudeArgs = new[]
                {
                    "--strict-mcp-config",
                    "--mcp-config", Workspace.McpConfigPath(_hostPort),
                };

                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                if (string.IsNullOrEmpty(claudePath))
                {
                    var fallbackApp = isWindows ? "cmd.exe" : "/bin/bash";
                    var fallbackArgs = isWindows ? Array.Empty<string>() : new[] { "-l" };
                    Log.Write($"[CoRhino] `claude` CLI not found on PATH. Falling back to {fallbackApp}. PATH=" + (loginPath ?? "<none>"));
                    await _session.StartAsync(fallbackApp, fallbackArgs, cwd, env).ConfigureAwait(false);
                    Log.Write($"[CoRhino] {fallbackApp} spawned in {cwd}");
                }
                else
                {
                    Log.Write($"[CoRhino] Spawning {claudePath} in {cwd}");
                    if (isWindows && !claudePath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only .exe can be launched directly by CreateProcess on Windows.
                        // .cmd / .bat / extensionless shell-shims (npm, fnm) must go through cmd.exe.
                        await _session.StartAsync("cmd.exe", new[] { "/c", claudePath! }.Concat(claudeArgs).ToArray(), cwd, env).ConfigureAwait(false);
                    }
                    else
                    {
                        await _session.StartAsync(claudePath!, claudeArgs, cwd, env).ConfigureAwait(false);
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
                var type = root.GetProperty("type").GetString();

                switch (type)
                {
                    case "ready":
                        _webReady = true;
                        Log.Write("[CoRhino] WebView ready.");
                        FlushPending();
                        break;

                    case "stdin":
                        if (root.TryGetProperty("data", out var dataEl))
                        {
                            var s = dataEl.GetString();
                            if (s != null)
                                _session.Write(Encoding.UTF8.GetBytes(s));
                        }
                        break;

                    case "resize":
                        if (root.TryGetProperty("cols", out var colsEl) &&
                            root.TryGetProperty("rows", out var rowsEl))
                        {
                            _session.Resize(colsEl.GetInt32(), rowsEl.GetInt32());
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
            catch (JsonException ex)
            {
                Log.Write($"[CoRhino] WebView JSON parse error: {ex.Message}");
            }
        }

        void OnPtyData(byte[] data)
        {
            var s = Encoding.UTF8.GetString(data);
            Application.Instance?.AsyncInvoke(() =>
            {
                if (!_webReady)
                {
                    _pendingOutput.Enqueue(s);
                    return;
                }
                var encoded = JsonSerializer.Serialize(s);
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onPtyData({encoded})");
            });
        }

        void FlushPending()
        {
            while (_pendingOutput.Count > 0)
            {
                var s = _pendingOutput.Dequeue();
                var encoded = JsonSerializer.Serialize(s);
                _ = _webView.ExecuteScriptAsync($"window.CoRhinoPanel?.onPtyData({encoded})");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RhinoDoc.EndOpenDocument -= OnDocChangedRestartHost;
                RhinoDoc.NewDocument -= OnDocChangedRestartHost;
                _session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
