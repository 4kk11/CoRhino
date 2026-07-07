using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        // Last size requested via Resize(). The UI reports the pane's real size as
        // soon as its xterm exists, which races SpawnAsync (on mac, CliResolver's
        // login shell alone takes hundreds of ms) — dropping that message would
        // leave claude on the 80x24 spawn default until the user resizes the panel
        // by hand. Remembered here and applied at/after spawn instead.
        readonly object _sizeLock = new object();
        int _lastCols, _lastRows;
        bool _hasLastSize;

        public event Action<string>? DataReceived;
        public event Action<int>? Exited;

        public bool IsRunning => _connection != null;

        public async Task StartAsync(string app, IReadOnlyList<string> args, string cwd, IDictionary<string, string>? env = null, int cols = 80, int rows = 24)
        {
            if (_connection != null)
                throw new InvalidOperationException("PtySession already started.");

            // A Resize() that already arrived (the UI's initial report beats the
            // spawn) supersedes the caller's default size.
            lock (_sizeLock)
            {
                if (_hasLastSize) { cols = _lastCols; rows = _lastRows; }
            }

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
            // Apply a size that arrived mid-spawn (Resize() saw _connection == null
            // then and could only record it). Redundant when nothing changed —
            // the PTY treats a same-size resize as a no-op. Best-effort: Porta.Pty's
            // Resize throws on failure (ConPTY ResizePseudoConsole / ioctl), and a
            // Dispose racing this apply can hand it a dead handle — neither may
            // bubble into the caller's catch, which would tear down a healthy tab.
            lock (_sizeLock)
            {
                if (_hasLastSize)
                {
                    try { connection.Resize(_lastCols, _lastRows); }
                    catch (Exception ex) { Log.Write($"[Claude/PTY] Post-spawn resize failed: {ex.Message}"); }
                }
            }
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
            // Decode here, statefully — NOT per-chunk downstream. A multi-byte UTF-8
            // sequence can straddle two reads; per-chunk GetString turns the split
            // char into U+FFFD×2-3, WIDENING the line claude drew (a rule of 63 `─`
            // became 65 cells) — xterm wraps it and ink's row arithmetic breaks,
            // leaving stale fragments around the input box. The Decoder instance
            // holds the partial sequence between reads, so every char comes out
            // exactly once and intact.
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = await _connection.ReaderStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read <= 0) break;
                    int n = decoder.GetChars(buffer, 0, read, chars, 0);
                    if (n > 0) DataReceived?.Invoke(new string(chars, 0, n));
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
            // The lock pairs with StartAsync's post-spawn apply: either _connection
            // is visible here and the resize goes straight through, or the size is
            // recorded and StartAsync applies it — never silently lost.
            lock (_sizeLock)
            {
                _lastCols = cols;
                _lastRows = rows;
                _hasLastSize = true;
                _connection?.Resize(cols, rows);
            }
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
