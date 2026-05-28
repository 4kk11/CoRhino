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
        string? _currentCwd;

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

            RhinoDoc.EndOpenDocument += OnDocChanged;
            RhinoDoc.NewDocument += OnDocChanged;

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
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                _currentCwd = ResolveCwd(home);

                var env = new System.Collections.Generic.Dictionary<string, string>();
                if (!string.IsNullOrEmpty(loginPath)) env["PATH"] = loginPath!;

                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                if (string.IsNullOrEmpty(claudePath))
                {
                    var fallbackApp = isWindows ? "cmd.exe" : "/bin/bash";
                    var fallbackArgs = isWindows ? Array.Empty<string>() : new[] { "-l" };
                    Log.Write($"[Claude] `claude` CLI not found on PATH. Falling back to {fallbackApp}. PATH=" + (loginPath ?? "<none>"));
                    await _session.StartAsync(fallbackApp, fallbackArgs, _currentCwd, env).ConfigureAwait(false);
                    Log.Write($"[Claude] {fallbackApp} spawned in {_currentCwd}");
                }
                else
                {
                    Log.Write($"[Claude] Spawning {claudePath} in {_currentCwd}");
                    if (isWindows && claudePath!.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        // .cmd/.bat cannot be launched directly by CreateProcess — go through cmd.exe.
                        await _session.StartAsync("cmd.exe", new[] { "/c", claudePath! }, _currentCwd, env).ConfigureAwait(false);
                    }
                    else
                    {
                        await _session.StartAsync(claudePath!, Array.Empty<string>(), _currentCwd, env).ConfigureAwait(false);
                    }
                    Log.Write($"[Claude] claude spawned in {_currentCwd}");
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

        static string ResolveCwd(string home)
        {
            try
            {
                var docPath = Rhino.RhinoDoc.ActiveDoc?.Path;
                if (!string.IsNullOrEmpty(docPath))
                {
                    var dir = Path.GetDirectoryName(docPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        return dir!;
                }
            }
            catch { }
            return home;
        }

        void OnDocChanged(object? sender, Rhino.DocumentEventArgs e)
        {
            if (!_session.IsRunning) return;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var newCwd = ResolveCwd(home);
            if (newCwd == _currentCwd) return;
            Log.Write($"[Claude] doc changed; sending `cd` to update cwd: {newCwd}");
            // Send a cd line to the live shell/claude. The CLI shows it as a typed command.
            _session.Write(Encoding.UTF8.GetBytes($"cd \"{newCwd.Replace("\"", "\\\"")}\"\n"));
            _currentCwd = newCwd;
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
                RhinoDoc.EndOpenDocument -= OnDocChanged;
                RhinoDoc.NewDocument -= OnDocChanged;
                _session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
