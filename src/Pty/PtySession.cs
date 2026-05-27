using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Porta.Pty;
using Rhino;
using RhinoClaudePanel;

namespace RhinoClaudePanel.Pty
{
    public class PtySession : IDisposable
    {
        IPtyConnection? _connection;
        CancellationTokenSource? _readerCts;
        Task? _readerTask;

        public event Action<byte[]>? DataReceived;
        public event Action<int>? Exited;

        public bool IsRunning => _connection != null;

        public async Task StartAsync(string app, IReadOnlyList<string> args, string cwd, IDictionary<string, string>? env = null, int cols = 80, int rows = 24)
        {
            if (_connection != null)
                throw new InvalidOperationException("PtySession already started.");

            Log.Write("[Claude/PTY] Preparing environment (minimal allow-list)...");
            var environment = new Dictionary<string, string>();
            var allowList = new[] { "HOME", "USER", "LOGNAME", "PATH", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR" };
            foreach (var key in allowList)
            {
                var v = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(v)) environment[key] = v!;
            }
            if (env != null)
                foreach (var kv in env) environment[kv.Key] = kv.Value;
            environment["TERM"] = environment.TryGetValue("TERM", out var t) ? t : "xterm-256color";
            Log.Write($"[Claude/PTY] env keys: {string.Join(",", environment.Keys)}");

            var options = new PtyOptions
            {
                Name = "rhino-claude-pty",
                Cols = cols,
                Rows = rows,
                Cwd = cwd,
                App = app,
                CommandLine = args is string[] arr ? arr : System.Linq.Enumerable.ToArray(args),
                Environment = environment,
            };

            Log.Write($"[Claude/PTY] Calling PtyProvider.SpawnAsync app={app} cwd={cwd} cols={cols} rows={rows}");
            _connection = await PtyProvider.SpawnAsync(options, CancellationToken.None).ConfigureAwait(false);
            Log.Write($"[Claude/PTY] SpawnAsync returned. pid={_connection?.Pid}");
            _connection.ProcessExited += (_, e) =>
            {
                Exited?.Invoke(e.ExitCode);
            };

            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoop(_readerCts.Token));
        }

        async Task ReadLoop(CancellationToken ct)
        {
            if (_connection == null) return;
            var buffer = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = await _connection.ReaderStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read <= 0) break;
                    var chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);
                    DataReceived?.Invoke(chunk);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write($"[Claude] PTY read error: {ex.Message}");
            }
        }

        public void Write(byte[] data)
        {
            if (_connection == null) return;
            _connection.WriterStream.Write(data, 0, data.Length);
            _connection.WriterStream.Flush();
        }

        public void Resize(int cols, int rows)
        {
            _connection?.Resize(cols, rows);
        }

        public void Dispose()
        {
            try { _readerCts?.Cancel(); } catch { }
            try { _connection?.Kill(); } catch { }
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            _readerCts = null;
            _readerTask = null;
        }
    }
}
