using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
                RhinoApp.WriteLine($"[Claude] Inspector enable failed: {ex.Message}");
            }
#endif

            _session.DataReceived += OnPtyData;
            _session.Exited += code =>
                RhinoApp.WriteLine($"[Claude] PTY exited with code {code}");

            Content = _webView;
            LoadWebView();

            RhinoApp.WriteLine("[Claude] Scheduling StartSession on background thread...");
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await StartSession().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Claude] StartSession unhandled: {ex.GetType().Name}: {ex.Message}");
                    RhinoApp.WriteLine($"[Claude] Stack: {ex.StackTrace}");
                }
            });
        }

        void LoadWebView()
        {
            var devUrl = Environment.GetEnvironmentVariable("RHINOCLAUDE_WEBUI_URL");
            if (devUrl != null)
                _webView.Url = new Uri(devUrl);
            else
                _webView.LoadHtml(LoadEmbeddedHtml());
        }

        static string LoadEmbeddedHtml()
        {
            var assembly = typeof(ClaudePanel).Assembly;
            var resourceName = assembly
                .GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith("Resources.Panel.index.html", StringComparison.Ordinal));
            if (resourceName == null)
                throw new InvalidOperationException("RhinoClaudePanel HTML resource not found.");
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Cannot open resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        async System.Threading.Tasks.Task StartSession()
        {
            RhinoApp.WriteLine("[Claude] StartSession() entered.");
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                RhinoApp.WriteLine($"[Claude] About to spawn /bin/bash in {home}");
                await _session.StartAsync("/bin/bash", new[] { "-l" }, home).ConfigureAwait(false);
                RhinoApp.WriteLine("[Claude] bash spawned.");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Claude] PTY start failed: {ex.GetType().Name}: {ex.Message}");
                RhinoApp.WriteLine($"[Claude] Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    RhinoApp.WriteLine($"[Claude] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
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
                        RhinoApp.WriteLine("[Claude] WebView ready.");
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
                }
            }
            catch (JsonException ex)
            {
                RhinoApp.WriteLine($"[Claude] WebView JSON parse error: {ex.Message}");
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
                _session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
