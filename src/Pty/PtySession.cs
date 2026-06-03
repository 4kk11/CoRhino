using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Porta.Pty;
using Rhino;
using CoRhino;

namespace CoRhino.Pty
{
    public class PtySession : IDisposable
    {
        IPtyConnection? _connection;
        CancellationTokenSource? _readerCts;
        Task? _readerTask;
        int _exitedRaised;
        volatile bool _disposed;

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
            var connection = await PtyProvider.SpawnAsync(options, CancellationToken.None).ConfigureAwait(false);
            _connection = connection;
            // Dispose() may have run while we were awaiting SpawnAsync (e.g. the tab was
            // closed mid-start). It sets _disposed first, then tries to Kill _connection
            // — which was still null at that instant. Re-check here and tear the just-
            // created process down ourselves so we never leak a live PTY.
            if (_disposed)
            {
                Log.Write("[Claude/PTY] Disposed during spawn; tearing down new connection.");
                _connection = null;
                try { connection.Kill(); } catch { }
                try { connection.Dispose(); } catch { }
                return;
            }
            Log.Write($"[Claude/PTY] SpawnAsync returned. pid={connection.Pid}");
            connection.ProcessExited += (_, e) =>
            {
                // claude exited (e.g. /exit). Tear the connection down so IsRunning
                // flips to false and the panel can spawn a fresh session. EOF in
                // ReadLoop reaches the same path; HandleExit dedupes via _exitedRaised.
                HandleExit(e.ExitCode);
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
                Log.Write($"[CoRhino] PTY read error: {ex.Message}");
            }

            // EOF on the master fd means the child closed its end (exit). On some
            // hosts ProcessExited may not fire, so treat EOF as an exit signal too.
            // HandleExit is idempotent, so a later ProcessExited is a no-op.
            if (!ct.IsCancellationRequested)
                HandleExit(0);
        }

        // Idempotent teardown of a finished session: dispose the connection, flip
        // IsRunning to false, and raise Exited exactly once. Safe to call from the
        // ProcessExited callback, ReadLoop EOF, or Dispose. Does NOT touch the
        // reader CTS (Dispose owns that) so it can run from inside the reader task.
        void HandleExit(int exitCode)
        {
            if (Interlocked.Exchange(ref _exitedRaised, 1) != 0) return;
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            Exited?.Invoke(exitCode);
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
            // Set _disposed before touching _connection so a spawn still in flight
            // (StartAsync awaiting SpawnAsync) sees it and tears down its own result.
            _disposed = true;
            // Mark exited first so the reader's EOF / ProcessExited paths don't also
            // raise Exited while we're tearing the session down explicitly.
            Interlocked.Exchange(ref _exitedRaised, 1);
            try { _readerCts?.Cancel(); } catch { }
            try { _connection?.Kill(); } catch { }
            try { _connection?.Dispose(); } catch { }
            _connection = null;
            _readerCts = null;
            _readerTask = null;
        }
    }
}
