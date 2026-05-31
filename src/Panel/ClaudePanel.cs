using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Eto.Forms;
using Rhino;
using RhinoClaudePanel.Pty;

namespace RhinoClaudePanel.Panel
{
    [System.Runtime.InteropServices.Guid("4C13D5E0-9E5A-4F18-9C2A-1E9E1A7B3C02")]
    public class ClaudePanel : Eto.Forms.Panel
    {
        readonly WebView _webView;
        readonly PtySession _session = new();
        readonly System.Collections.Generic.Queue<string> _pendingOutput = new();
        bool _webReady;

        // The RhinoMCP host port this panel owns. Chosen once, free at construction
        // time, so each Rhino instance gets a distinct endpoint. Both MCPStart and
        // the generated .mcp.json use it, keeping claude bound to *this* Rhino.
        readonly int _hostPort = Workspace.ResolveFreePort();

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ClaudePanel(uint documentSerialNumber)
        {
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
                Log.Write($"[Claude] Inspector enable failed: {ex.Message}");
            }
#endif

            _session.DataReceived += OnPtyData;
            _session.Exited += code =>
                Log.Write($"[Claude] PTY exited with code {code}");

            // Start the in-process RhinoMCP HTTP host in *this* Rhino on our own
            // port, and keep it bound to the active document. The host is per-doc,
            // so re-fire on open/new. claude connects to it via the session .mcp.json.
            // If Rhino-MCP-Platform isn't installed there is no host to start, so run
            // the install onboarding instead (Phase 2a) and skip MCPStart until the
            // user restarts Rhino with the package present.
            Log.Write($"[Claude] Selected host MCP port {_hostPort}.");
            RhinoDoc.EndOpenDocument += OnDocChangedRestartHost;
            RhinoDoc.NewDocument += OnDocChangedRestartHost;
            if (RhinoMcpInstaller.IsInstalled())
            {
                EnsureHostMcpStarted();
            }
            else
            {
                Log.Write("[Claude] Rhino-MCP-Platform not installed; starting install onboarding.");
                RhinoMcpInstaller.RunOnboarding();
            }

            Content = _webView;
            LoadWebView();

            Log.Write("[Claude] Scheduling StartSession on background thread...");
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await StartSession().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Write($"[Claude] StartSession unhandled: {ex.GetType().Name}: {ex.Message}");
                    Log.Write($"[Claude] Stack: {ex.StackTrace}");
                }
            });
        }

        void LoadWebView()
        {
            var devUrl = Environment.GetEnvironmentVariable("RHINOCLAUDE_WEBUI_URL");
            if (devUrl != null)
            {
                _webView.Url = new Uri(devUrl);
                return;
            }

            var pluginDir = Path.GetDirectoryName(typeof(ClaudePanel).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot resolve plugin directory.");
            var indexPath = Path.Combine(pluginDir, "web", "index.html");
            if (!File.Exists(indexPath))
            {
                Log.Write($"[Claude] web/index.html not found at {indexPath}");
                return;
            }
            var html = File.ReadAllText(indexPath);
            _webView.LoadHtml(html);
            Log.Write($"[Claude] LoadHtml invoked, html length={html.Length}");
        }

        async System.Threading.Tasks.Task StartSession()
        {
            Log.Write("[Claude] StartSession() entered.");
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
                    Log.Write($"[Claude] `claude` CLI not found on PATH. Falling back to {fallbackApp}. PATH=" + (loginPath ?? "<none>"));
                    await _session.StartAsync(fallbackApp, fallbackArgs, cwd, env).ConfigureAwait(false);
                    Log.Write($"[Claude] {fallbackApp} spawned in {cwd}");
                }
                else
                {
                    Log.Write($"[Claude] Spawning {claudePath} in {cwd}");
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
                    Log.Write($"[Claude] claude spawned in {cwd}");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[Claude] PTY start failed: {ex.GetType().Name}: {ex.Message}");
                Log.Write($"[Claude] Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    Log.Write($"[Claude] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }

        void EnsureHostMcpStarted()
        {
            // RhinoApp.RunScript must not run nested inside another command, but the
            // panel is often constructed *from* the RhinoClaude command. Defer to the
            // next Idle so MCPStart runs as a top-level script (otherwise the nested
            // RunScript fails and the host never comes up).
            var port = _hostPort;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                RhinoApp.Idle -= handler;
                // MCPStart prompts for a port; the `-` dash form feeds it from script.
                // StartOrRestart semantics make re-firing on each doc safe. If the
                // RhinoMCP plugin isn't installed the command is unknown → RunScript
                // returns false; we log and move on (onboarding handles install).
                try
                {
                    var ok = RhinoApp.RunScript($"_-MCPStart {port}\n", false);
                    Log.Write(ok
                        ? $"[Claude] MCPStart {port} fired (host MCP up)."
                        : $"[Claude] MCPStart failed (Rhino-MCP-Platform not installed?).");
                }
                catch (Exception ex)
                {
                    Log.Write($"[Claude] MCPStart threw: {ex.GetType().Name}: {ex.Message}");
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
                        Log.Write("[Claude] WebView ready.");
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
                Log.Write($"[Claude] WebView JSON parse error: {ex.Message}");
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
                _ = _webView.ExecuteScriptAsync($"window.ClaudePanel?.onPtyData({encoded})");
            });
        }

        void FlushPending()
        {
            while (_pendingOutput.Count > 0)
            {
                var s = _pendingOutput.Dequeue();
                var encoded = JsonSerializer.Serialize(s);
                _ = _webView.ExecuteScriptAsync($"window.ClaudePanel?.onPtyData({encoded})");
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
