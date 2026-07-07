using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CoRhino.Cli;
using CoRhino.Pty;

namespace CoRhino.Panel
{
    /// <summary>
    /// Scans codex's rollout store (~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl)
    /// for the sessions recorded under CoRhino's shared workspace. The codex
    /// counterpart of SessionsCatalog, same regime: knows nothing about the
    /// WebView or tabs, keeps an allowlist of what the last scan actually saw
    /// (resume/delete only accept ids from that set), and runs on a background
    /// thread.
    ///
    /// Differences that shape the implementation (spike-verified, see
    /// plans/codex-cli-support-design.md 5.5):
    /// - Sessions of EVERY cwd live in one date-partitioned tree, so the scan
    ///   walks all of it and filters by the first line's session_meta.cwd. That
    ///   first line is immutable → cached per path; only content-dependent bits
    ///   are re-read when the file's mtime moves.
    /// - The auto title comes from ~/.codex/session_index.jsonl (thread_name).
    ///   The index lags/misses sessions (exec sessions never appear), so it is
    ///   best-effort with the first user prompt as fallback.
    /// - Deletion goes through `codex delete <id>` so codex keeps its index and
    ///   archive bookkeeping consistent — NOT a raw file delete.
    /// </summary>
    static class CodexSessionsCatalog
    {
        sealed record KnownSession(string RolloutPath, string Title);

        // sessionId → facts from the most recent Scan. Replaced wholesale (never
        // mutated) → volatile read is enough. Same pattern as SessionsCatalog.
        static volatile Dictionary<string, KnownSession>? _known;

        // path → parsed head facts. session_meta (id/cwd/started) never changes;
        // HasContent/FirstPrompt can flip once the user talks, so entries are
        // refreshed when the mtime moves. Guarded by a lock: scans are
        // single-flight today, but that regime lives in the panel — don't
        // depend on it here.
        sealed record HeadFacts(
            DateTime MtimeUtc, string? SessionId, string? Cwd,
            DateTime? StartedAtUtc, string? FirstPrompt, bool HasContent);
        static readonly Dictionary<string, HeadFacts> HeadCache = new(StringComparer.Ordinal);
        static readonly object CacheGate = new();

        static string CodexHome => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        static string SessionsRoot => Path.Combine(CodexHome, "sessions");
        static string SessionIndexPath => Path.Combine(CodexHome, "session_index.jsonl");

        public static IReadOnlyList<SessionInfo> Scan()
        {
            var sessions = new List<SessionInfo>();
            var known = new Dictionary<string, KnownSession>(StringComparer.Ordinal);
            try
            {
                ScanRollouts(SessionNames.LoadAll(), LoadIndexTitles(), sessions, known);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] Codex sessions scan failed: {ex.GetType().Name}: {ex.Message}");
            }
            _known = known;
            return sessions.OrderByDescending(s => s.LastActivityUtc).ToList();
        }

        /// <summary>Resume/delete gate: only sessions the last scan listed are actionable.</summary>
        public static bool TryGetKnown(string sessionId, out string rolloutPath)
        {
            rolloutPath = string.Empty;
            if (_known == null || !_known.TryGetValue(sessionId, out var k)) return false;
            rolloutPath = k.RolloutPath;
            return true;
        }

        /// <summary>Auto title from the last scan, for tab labels. False for unscanned/fresh ids.</summary>
        public static bool TryGetKnownTitle(string sessionId, out string title)
        {
            title = string.Empty;
            if (_known == null || !_known.TryGetValue(sessionId, out var k)) return false;
            title = k.Title;
            return true;
        }

        /// <summary>
        /// Delete a codex session via `codex delete &lt;id&gt;`. Blocking (bounded
        /// 10 s) — call on a background thread. Returns false on any failure;
        /// the caller keeps the row listed so nothing silently vanishes.
        /// </summary>
        public static bool Delete(string sessionId)
        {
            var (codexPath, _) = CliResolver.Resolve(AgentCliRegistry.Codex.BinaryName);
            if (codexPath == null)
            {
                Log.Write($"[CoRhino] codex delete skipped: codex CLI not found on PATH.");
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = codexPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("delete");
                // --force: without it codex prompts for confirmation and dies on a
                // non-interactive terminal. The drawer's two-step 🗑 confirm is the
                // consent; requires SESSION to be a UUID, which our allowlist ids are.
                psi.ArgumentList.Add("--force");
                psi.ArgumentList.Add(sessionId);
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                if (!proc.WaitForExit(10_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    Log.Write($"[CoRhino] codex delete timed out ({sessionId}).");
                    return false;
                }
                if (proc.ExitCode != 0)
                {
                    var err = proc.StandardError.ReadToEnd().Trim();
                    Log.Write($"[CoRhino] codex delete failed ({sessionId}, exit {proc.ExitCode}): {err}");
                    return false;
                }
                Log.Write($"[CoRhino] Deleted codex session {sessionId}.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] codex delete threw ({sessionId}): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------

        // id → thread_name from session_index.jsonl (last line per id wins —
        // codex appends re-title events). Missing/corrupt file → empty map.
        static Dictionary<string, string> LoadIndexTitles()
        {
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(SessionIndexPath)) return titles;
                foreach (var line in File.ReadLines(SessionIndexPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                        if (!root.TryGetProperty("thread_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                        var id = idEl.GetString();
                        var name = nameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                            titles[id!] = name!;
                    }
                    catch (JsonException) { /* torn line — skip */ }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] session_index.jsonl unreadable (titles degrade to prompts): {ex.GetType().Name}: {ex.Message}");
            }
            return titles;
        }

        static void ScanRollouts(Dictionary<string, string> names, Dictionary<string, string> indexTitles,
            List<SessionInfo> into, Dictionary<string, KnownSession> known)
        {
            if (!Directory.Exists(SessionsRoot)) return;
            foreach (var file in Directory.EnumerateFiles(SessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var facts = GetHeadFacts(file);
                    if (facts.SessionId == null || facts.Cwd == null) continue;
                    if (!PathsEqual(facts.Cwd, Workspace.WorkspaceRoot)) continue;
                    // Codex writes the rollout at spawn, before any user input —
                    // without this filter every codex tab ever opened leaves a row.
                    if (!facts.HasContent) continue;

                    var id = facts.SessionId;
                    var title = CleanTitle(indexTitles.TryGetValue(id, out var tn) ? tn : facts.FirstPrompt)
                        ?? id.Substring(0, Math.Min(8, id.Length));
                    into.Add(new SessionInfo
                    {
                        SessionId = id,
                        Title = title,
                        CustomName = names.TryGetValue(id, out var n) ? n : null,
                        StartedAtUtc = facts.StartedAtUtc ?? File.GetCreationTimeUtc(file),
                        LastActivityUtc = facts.MtimeUtc,
                        Cli = AgentCliKind.Codex,
                    });
                    known[id] = new KnownSession(file, title);
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] Codex session read failed ({file}): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        static HeadFacts GetHeadFacts(string file)
        {
            var mtime = File.GetLastWriteTimeUtc(file);
            lock (CacheGate)
            {
                if (HeadCache.TryGetValue(file, out var hit) && hit.MtimeUtc == mtime)
                    return hit;
            }
            var facts = ExtractHead(file, mtime);
            lock (CacheGate)
            {
                HeadCache[file] = facts;
            }
            return facts;
        }

        const int HeadChunk = 256 * 1024;

        // First line: {"type":"session_meta","payload":{"id","cwd","timestamp",…}}.
        // The rest of the head chunk is scanned for the first user_message event
        // ({"type":"event_msg","payload":{"type":"user_message","message":…}}) —
        // its presence is what makes the session real (worth listing), and its
        // text is the title fallback. A file bigger than the chunk has turns by
        // definition (an untouched session is a few KB of meta+instructions),
        // so it counts as having content even when the user line sits past the
        // chunk boundary.
        static HeadFacts ExtractHead(string file, DateTime mtimeUtc)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length; // snapshot once (concurrent append)
            var buf = new byte[Math.Min(length, HeadChunk)];
            var read = ReadFully(fs, buf);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n');
            var truncated = read < length;
            var usable = truncated ? lines.Length - 1 : lines.Length;

            string? sessionId = null, cwd = null, firstPrompt = null;
            DateTime? startedAt = null;
            var hasUser = false;

            for (var i = 0; i < usable; i++)
            {
                var line = lines[i];
                if (i == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var t) && t.GetString() == "session_meta" &&
                            root.TryGetProperty("payload", out var payload))
                        {
                            if (payload.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                sessionId = idEl.GetString();
                            if (payload.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                                cwd = cwdEl.GetString();
                            if (payload.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String &&
                                DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                                startedAt = ts.UtcDateTime;
                        }
                    }
                    catch (JsonException) { }
                    if (sessionId == null || cwd == null) break; // not a rollout we understand
                    continue;
                }

                if (!line.Contains("\"user_message\"")) continue; // cheap pre-filter
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    if (!payload.TryGetProperty("type", out var pt) || pt.GetString() != "user_message") continue;
                    hasUser = true;
                    if (payload.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                    {
                        var msg = msgEl.GetString();
                        if (!string.IsNullOrWhiteSpace(msg)) firstPrompt = msg;
                    }
                    break;
                }
                catch (JsonException) { /* torn or odd line — skip */ }
            }

            return new HeadFacts(mtimeUtc, sessionId, cwd, startedAt, firstPrompt, hasUser || truncated);
        }

        static bool PathsEqual(string a, string b) =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        // Single display line, capped — prompts can be multi-KB pastes.
        static string? CleanTitle(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t.Length > 120 ? t.Substring(0, 120) + "…" : t;
        }

        static int ReadFully(Stream s, byte[] buf)
        {
            var total = 0;
            while (total < buf.Length)
            {
                var n = s.Read(buf, total, buf.Length - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }
}
