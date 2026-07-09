using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// One past session as shown in the Sessions Drawer, from either CLI's
    /// catalog. Raw facts only — live decoration happens in CoRhinoPanel on the
    /// UI thread, where the tab state is readable.
    /// </summary>
    sealed class SessionInfo
    {
        public required string SessionId { get; init; }
        /// <summary>Auto title (claude: last ai-title/prompt; codex: thread_name/first prompt), else id prefix.</summary>
        public required string Title { get; init; }
        /// <summary>User-given name from session-names.json, or null.</summary>
        public string? CustomName { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required DateTime LastActivityUtc { get; init; }
        /// <summary>Which CLI recorded this session (drives the drawer badge and resume dispatch).</summary>
        public CoRhino.Cli.AgentCliKind Cli { get; init; } = CoRhino.Cli.AgentCliKind.Claude;
    }

    /// <summary>
    /// Scans claude's transcript store for the sessions recorded under CoRhino's
    /// shared workspace — one project dir (~/.claude/projects/&lt;workspace-slug&gt;/),
    /// since every session shares the one cwd. Like SkillsCatalog it knows nothing
    /// about the WebView or tabs, and it keeps an allowlist of what the last scan
    /// actually saw: resumeSession only accepts sessionIds from that set, so the
    /// WebView can never make the panel spawn `--resume` with an arbitrary id.
    /// Scan() does chunked file I/O (transcripts run to MBs) — call it on a
    /// background thread and marshal the result to the UI thread.
    /// </summary>
    static class SessionsCatalog
    {
        sealed record KnownSession(string JsonlPath, string Title);

        // sessionId → facts from the most recent Scan. Replaced wholesale (never
        // mutated) → volatile read is enough. Same pattern as SkillsCatalog._knownPaths.
        static volatile Dictionary<string, KnownSession>? _known;

        static string ClaudeProjectsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        public static IReadOnlyList<SessionInfo> Scan()
        {
            var sessions = new List<SessionInfo>();
            var known = new Dictionary<string, KnownSession>(StringComparer.Ordinal);
            try
            {
                ScanProjectDir(SessionNames.LoadAll(), sessions, known);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] Sessions scan failed: {ex.GetType().Name}: {ex.Message}");
            }
            _known = known;
            return sessions.OrderByDescending(s => s.LastActivityUtc).ToList();
        }

        /// <summary>Resume gate: only sessions the last scan listed are spawnable.</summary>
        public static bool TryGetKnown(string sessionId, out string jsonlPath)
        {
            jsonlPath = string.Empty;
            if (_known == null || !_known.TryGetValue(sessionId, out var k)) return false;
            jsonlPath = k.JsonlPath;
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
        /// claude's project-dir encoding of a cwd: every char outside ASCII
        /// [A-Za-z0-9] becomes '-' (deliberately NOT char.IsLetterOrDigit — that
        /// would keep e.g. Japanese letters that claude's encoder replaces, and
        /// the drawer would silently go empty on non-ASCII home paths). Verified
        /// on macOS ("/Users/x/.corhino/…" → "-Users-x--corhino-…"). UNVERIFIED
        /// on Windows ("C:\…" — how '\' and ':' encode must be checked before the
        /// feature is called done there); kept a pure function so the platform
        /// branch has one home.
        /// </summary>
        /// <summary>
        /// The transcript jsonl a workspace claude session writes, derived —
        /// not discovered — from the session id (cwd is the one shared
        /// workspace, see D10/D11). Exposed for the orchestration manifest and
        /// spawn responses: an orchestrator reads its child's final report here.
        /// The file may not exist yet (claude creates it lazily).
        /// </summary>
        internal static string TranscriptPathFor(string sessionId) =>
            Path.Combine(ClaudeProjectsRoot, ProjectSlug(Workspace.WorkspaceRoot), sessionId + ".jsonl");

        internal static string ProjectSlug(string cwd)
        {
            var sb = new StringBuilder(cwd.Length);
            foreach (var c in cwd)
            {
                var keep = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
                sb.Append(keep ? c : '-');
            }
            return sb.ToString();
        }

        static void ScanProjectDir(Dictionary<string, string> names,
            List<SessionInfo> into, Dictionary<string, KnownSession> known)
        {
            var projectDir = Path.Combine(ClaudeProjectsRoot, ProjectSlug(Workspace.WorkspaceRoot));
            string[] files;
            try
            {
                if (!Directory.Exists(projectDir)) return;
                // Top level only: subagent transcripts live in <sessionId>/subagents/
                // subdirectories and must not be listed as sessions.
                files = Directory.GetFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] Sessions scan failed ({projectDir}): {ex.GetType().Name}: {ex.Message}");
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (!Guid.TryParse(stem, out _)) continue;

                    var meta = ExtractMeta(file);
                    // Claude writes a jsonl at spawn time, before any user input —
                    // without this filter every tab ever opened leaves a junk row.
                    if (!meta.HasContent) continue;

                    var title = CleanTitle(meta.Title) ?? stem.Substring(0, 8);
                    into.Add(new SessionInfo
                    {
                        SessionId = stem,
                        Title = title,
                        CustomName = names.TryGetValue(stem, out var n) ? n : null,
                        StartedAtUtc = meta.StartedAtUtc ?? File.GetCreationTimeUtc(file),
                        LastActivityUtc = File.GetLastWriteTimeUtc(file),
                    });
                    known[stem] = new KnownSession(file, title);
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] Session read failed ({file}): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        internal sealed record JsonlMeta(DateTime? StartedAtUtc, string? Title, bool HasContent);

        /// <summary>
        /// Cheap metadata from a transcript without reading the whole file: the head
        /// chunk yields the first real user prompt + its timestamp; the tail chunk,
        /// scanned backwards, yields the LAST ai-title (claude re-titles as the
        /// conversation evolves) with last-prompt as fallback.
        /// </summary>
        internal static JsonlMeta ExtractMeta(string path)
        {
            var (startedAt, firstPrompt, hasUser) = ParseHead(path);
            var (aiTitle, lastPrompt) = ParseTail(path);
            var title = aiTitle ?? lastPrompt ?? firstPrompt;
            // Any of the three proves a real conversation. lastPrompt matters when
            // the FIRST user line alone exceeds the head chunk (huge paste): the
            // head then sees no user line, but the session is obviously real.
            return new JsonlMeta(startedAt, title, hasUser || aiTitle != null || lastPrompt != null);
        }

        const int HeadChunk = 64 * 1024;
        const int TailChunk = 256 * 1024;

        static (DateTime? StartedAt, string? FirstPrompt, bool HasUser) ParseHead(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length; // snapshot once (concurrent append/truncate)
            var buf = new byte[HeadChunk];
            var read = ReadFully(fs, buf);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n');
            // If the chunk cut the file mid-line, the last element is a fragment.
            var usable = read < length ? lines.Length - 1 : lines.Length;

            DateTime? startedAt = null;
            string? firstPrompt = null;
            var hasUser = false;
            for (var i = 0; i < usable; i++)
            {
                var line = lines[i];
                if (!line.Contains("\"type\":\"user\"")) continue; // cheap pre-filter
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var t) || t.GetString() != "user") continue;
                    if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;

                    hasUser = true;
                    if (startedAt == null &&
                        root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    {
                        startedAt = ts.UtcDateTime;
                    }

                    // Command wrappers (<command-name>…) are user-typed slash
                    // commands, not a conversation opener — keep looking.
                    var prompt = ExtractUserText(root);
                    if (!string.IsNullOrWhiteSpace(prompt) && !prompt!.StartsWith("<", StringComparison.Ordinal))
                    {
                        firstPrompt = prompt;
                        break;
                    }
                }
                catch (JsonException) { /* torn or odd line — skip */ }
            }
            return (startedAt, firstPrompt, hasUser);
        }

        static (string? AiTitle, string? LastPrompt) ParseTail(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Snapshot the length once: claude can rewrite/truncate concurrently,
            // and a second Length read below the first would size the buffer
            // negative and throw.
            var length = fs.Length;
            var start = Math.Max(0, length - TailChunk);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[length - start];
            var read = ReadFully(fs, buf);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            if (start > 0)
            {
                // Discard up to the first newline so a mid-line (possibly mid-UTF-8)
                // seek can't corrupt the first parsed line.
                var nl = text.IndexOf('\n');
                text = nl >= 0 ? text.Substring(nl + 1) : string.Empty;
            }

            string? lastPrompt = null;
            var lines = text.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.Contains("\"type\":\"ai-title\""))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var t) && t.GetString() == "ai-title" &&
                            root.TryGetProperty("aiTitle", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                        {
                            var v = titleEl.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) return (v, lastPrompt);
                        }
                    }
                    catch (JsonException) { }
                }
                else if (lastPrompt == null && line.Contains("\"type\":\"last-prompt\""))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var t) && t.GetString() == "last-prompt" &&
                            root.TryGetProperty("lastPrompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String)
                        {
                            var v = promptEl.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) lastPrompt = v;
                        }
                    }
                    catch (JsonException) { }
                }
            }
            return (null, lastPrompt);
        }

        static string? ExtractUserText(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("content", out var content)) return null;
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object &&
                        block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                        block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    {
                        return txt.GetString();
                    }
                }
            }
            return null;
        }

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
