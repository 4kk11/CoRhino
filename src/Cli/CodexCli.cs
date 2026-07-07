using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoRhino.Panel;

namespace CoRhino.Cli
{
    /// <summary>
    /// The codex CLI adapter. Isolation stance (spike-verified 2026-07-07,
    /// codex-cli 0.142.5 — see plans/codex-cli-support-design.md D13/D14/D15):
    /// codex has no --strict-mcp-config equivalent and -c 'mcp_servers={...}'
    /// MERGES instead of replacing, so isolation is composed per spawn from
    /// (1) --disable plugins (kills plugin-injected MCP servers that never
    /// appear in config.toml), (2) enabled=false for every server actually in
    /// the user's config.toml, (3) the rhino server added on top. Nothing under
    /// ~/.codex is written. The layer instruction rides in -c
    /// developer_instructions (appends to instructions, invisible in the TUI).
    /// The session id is NOT assignable at spawn; it is discovered afterwards
    /// by matching a fresh rollout file whose session_meta.cwd is our
    /// workspace.
    /// </summary>
    internal sealed class CodexCli : IAgentCli
    {
        public AgentCliKind Kind => AgentCliKind.Codex;
        public string DisplayName => "Codex";
        public string BinaryName => "codex";
        // Skills reach codex through the workspace symlinks (see
        // Workspace.EnsureCodexSkillsLinks): user store on .agents/skills,
        // bundled cc-plugin on .codex/skills — the same files claude loads via
        // --plugin-dir. No in-session reload though: skills are injected once
        // at session start, so changes land in new tabs only.
        public bool SupportsSkillsDrawer => true;
        public bool SupportsSkillsReload => false;
        public bool LoadsSkillsViaWorkspaceMount => true;

        // Fresh spawns (sessionId null) mint a token — the id to derive from
        // doesn't exist yet; the {id → token} pair is persisted by the panel
        // once discovery lands. Known ids restore their stored token so resume
        // keeps the layer the session created its objects in; an id with no
        // stored token (discovery failed back then) gets one now.
        public string? LayerTokenFor(string? sessionId)
        {
            if (sessionId == null) return MintLayerToken();
            if (SessionLayers.TryGet(sessionId, out var stored)) return stored;
            var minted = MintLayerToken();
            SessionLayers.Set(sessionId, minted);
            return minted;
        }

        static string MintLayerToken() =>
            "s-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public IReadOnlyList<string> BuildArgs(SpawnContext ctx)
        {
            var args = new List<string>();

            if (ctx.Resume && ctx.SessionId != null)
            {
                args.Add("resume");
                args.Add(ctx.SessionId);
            }

            // (1) Plugins inject MCP servers (and tools) that config.toml never
            // mentions — disable the whole feature for this session.
            args.Add("--disable");
            args.Add("plugins");

            // (2) Disable every MCP server the user has registered. Only names
            // that actually exist may be disabled: enabled=false on a
            // nonexistent name is a config error that kills the launch.
            foreach (var name in ReadUserMcpServerNames())
            {
                args.Add("-c");
                args.Add($"mcp_servers.{TomlKeySegment(name)}.enabled=false");
            }

            // (3) This panel's rhino server. default_tools_approval_mode=auto →
            // rhino tools run without a TUI approval prompt. The header tags the
            // connection per agent (future host-side attribution/lease hook).
            args.Add("-c");
            args.Add(
                $"mcp_servers.rhino={{url={TomlString($"http://localhost:{ctx.HostPort}/")}," +
                $"http_headers={{{TomlString("X-CoRhino-Agent")}={TomlString(ctx.AgentId)}}}," +
                $"default_tools_approval_mode={TomlString("auto")}}}");

            // The workspace is CoRhino's own dir; opening the panel is the
            // consent. Without this codex's first-run trust prompt blocks the
            // session (and answering it would write to the user's config.toml).
            args.Add("-c");
            args.Add($"projects.{TomlKeySegment(ctx.Cwd)}.trust_level={TomlString("trusted")}");

            if (ctx.LayerToken != null)
            {
                // The parallel-agent layer. developer_instructions appends to
                // the session's instructions without replacing anything and
                // never shows in the TUI — codex's --append-system-prompt
                // equivalent. AGENTS.md holds the rules; this carries the one
                // per-session value it can't.
                args.Add("-c");
                args.Add("developer_instructions=" + TomlString(L10n.T(
                    $"このセッションの担当 Rhino レイヤは `{ctx.LayerToken}` です。" +
                    "オブジェクトの作成・変更・削除はこのレイヤ配下のみで行ってください" +
                    "（詳細は AGENTS.md の「並列エージェントのルール」）。",
                    $"This session's assigned Rhino layer is `{ctx.LayerToken}`. " +
                    "Create, modify, and delete objects only under this layer " +
                    "(see the parallel-agent rules in AGENTS.md).")));
            }

            return args;
        }

        // ------------------------------------------------------------------
        // User config scan (read-only)

        static string CodexHome =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        // [mcp_servers.<name>] / [mcp_servers."<name>"] / [mcp_servers.'<name>']
        // table headers, including deeper ones like [mcp_servers.foo.env] (the
        // first segment is the name). Basic-quoted names capture escape
        // sequences too; they are resolved (or rejected) in UnescapeBasicKey.
        static readonly Regex McpServerHeader = new(
            @"^\s*\[\s*mcp_servers\s*\.\s*(?:""(?<q>(?:[^""\\]|\\.)*)""|'(?<l>[^']*)'|(?<b>[A-Za-z0-9_-]+))",
            RegexOptions.Compiled);

        static readonly Regex McpServersInline = new(
            @"^\s*mcp_servers\s*=", RegexOptions.Compiled);

        /// <summary>
        /// The MCP server names registered in ~/.codex/config.toml. A light
        /// line-based scan of [mcp_servers.*] table headers — not a TOML parser.
        /// The failure direction is chosen deliberately: a name we cannot
        /// resolve with certainty is SKIPPED (it stays enabled and coexists —
        /// weaker isolation) rather than guessed (a disable for a nonexistent
        /// name is a config error that kills the launch). Known gaps, each
        /// logged when hit: unsupported escapes in quoted names, and a
        /// top-level inline `mcp_servers = {...}` table. Missing or unreadable
        /// file → empty.
        /// </summary>
        internal static IReadOnlyList<string> ReadUserMcpServerNames()
        {
            var path = Path.Combine(CodexHome, "config.toml");
            string[] lines;
            try
            {
                if (!File.Exists(path)) return Array.Empty<string>();
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] codex config.toml unreadable (spawning without disables): {ex.GetType().Name}: {ex.Message}");
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var line in lines)
            {
                if (McpServersInline.IsMatch(line))
                {
                    Log.Write("[CoRhino] codex config.toml has an inline mcp_servers table; its servers stay enabled (not isolated).");
                    continue;
                }
                var m = McpServerHeader.Match(line);
                if (!m.Success) continue;
                string? name;
                if (m.Groups["q"].Success) name = UnescapeBasicKey(m.Groups["q"].Value);
                else if (m.Groups["l"].Success) name = m.Groups["l"].Value; // literal string: verbatim
                else name = m.Groups["b"].Value;
                if (string.IsNullOrEmpty(name))
                {
                    Log.Write($"[CoRhino] codex config.toml server name not safely parseable; leaving it enabled: {line.Trim()}");
                    continue;
                }
                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }

        // Resolve TOML basic-string escapes in a quoted key. Only \" and \\ are
        // handled — anything fancier (\u..., \n) returns null so the caller
        // skips the name instead of emitting a disable for a wrong one.
        internal static string? UnescapeBasicKey(string raw)
        {
            if (!raw.Contains('\\')) return raw;
            var sb = new System.Text.StringBuilder(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\\') { sb.Append(raw[i]); continue; }
                if (i + 1 >= raw.Length) return null;
                var next = raw[++i];
                if (next == '"' || next == '\\') sb.Append(next);
                else return null;
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // TOML rendering (the -c value side is parsed as TOML by codex)

        /// <summary>A TOML basic string: "..." with backslash/quote/control escapes.</summary>
        internal static string TomlString(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>A dotted-path key segment: bare if possible, else quoted.</summary>
        internal static string TomlKeySegment(string name) =>
            Regex.IsMatch(name, "^[A-Za-z0-9_-]+$") ? name : TomlString(name);

        // ------------------------------------------------------------------
        // Session id discovery (D14)

        // Rollout files already matched to some tab, across all panels in this
        // process. Guarded by its own lock (discovery runs on background
        // threads). NOTE this set alone cannot ATTRIBUTE a rollout — every
        // codex tab shares one cwd, so two unclaimed matching rollouts are
        // indistinguishable. Attribution is guaranteed by the panel's spawn
        // gate (CoRhinoPanel serializes codex launch → discovery), which keeps
        // at most ONE unclaimed matching rollout in existence at a time; the
        // set then only has to prevent re-claiming across polls.
        static readonly HashSet<string> ClaimedRollouts = new(StringComparer.Ordinal);
        static readonly object ClaimGate = new();

        // rollout-2026-07-07T16-19-53-<uuid>.jsonl
        static readonly Regex RolloutName = new(
            @"^rollout-.*-(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl$",
            RegexOptions.Compiled);

        /// <summary>
        /// Poll ~/.codex/sessions for a rollout created by OUR spawn: file new
        /// enough (mtime ≥ spawn − 5 s), session_meta.cwd == our workspace, and
        /// not already claimed by another tab. The file appears within seconds
        /// of launch (spike-measured); 15 s covers slow starts. Null on
        /// timeout — the tab stays usable, it just can't be resumed later.
        ///
        /// Cancellation (the tab was removed) does NOT abort the search — it
        /// switches it to burn mode: OUR spawn's rollout may already be on disk,
        /// and while the panel's spawn gate is still held (it is: this task owns
        /// it) the next codex tab hasn't launched, so any matching unclaimed
        /// rollout is ours. It must be claimed anyway — just never attributed —
        /// or the next tab would mistake it for its own. Burn mode shortens the
        /// deadline: a process killed before writing its rollout never writes
        /// one, and the gate shouldn't be held the full window for nothing.
        /// </summary>
        public async Task<string?> DiscoverSessionIdAsync(SpawnContext ctx, CancellationToken ct)
        {
            var sessionsRoot = Path.Combine(CodexHome, "sessions");
            var earliest = ctx.SpawnedAtUtc.AddSeconds(-5);
            var deadline = ctx.SpawnedAtUtc.AddSeconds(15);
            var burning = false;

            while (DateTime.UtcNow < deadline)
            {
                if (!burning && ct.IsCancellationRequested)
                {
                    burning = true;
                    var burnDeadline = DateTime.UtcNow.AddSeconds(4);
                    if (burnDeadline < deadline) deadline = burnDeadline;
                }
                foreach (var file in EnumerateRecentRollouts(sessionsRoot, ctx.SpawnedAtUtc))
                {
                    string id;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < earliest) continue;
                        var m = RolloutName.Match(Path.GetFileName(file));
                        if (!m.Success) continue;
                        id = m.Groups["id"].Value;
                        if (!TryReadSessionMetaCwd(file, out var cwd) || !PathsEqual(cwd, ctx.Cwd)) continue;
                    }
                    catch (IOException) { continue; }   // mid-write; retry next poll
                    catch (JsonException) { continue; } // first line incomplete; retry

                    lock (ClaimGate)
                    {
                        if (ClaimedRollouts.Contains(file)) continue;
                        ClaimedRollouts.Add(file);
                    }
                    if (burning || ct.IsCancellationRequested)
                    {
                        Log.Write($"[CoRhino] Burned codex rollout {Path.GetFileName(file)} (its tab {ctx.AgentId} was removed mid-discovery).");
                        return null;
                    }
                    Log.Write($"[CoRhino] Discovered codex session {id} for {ctx.AgentId} ({Path.GetFileName(file)}).");
                    return id;
                }
                await Task.Delay(500).ConfigureAwait(false); // no ct: cancellation = burn mode, not abort
            }
            if (!burning)
                Log.Write($"[CoRhino] Codex session id discovery timed out for {ctx.AgentId} (tab stays usable; resume/rename unavailable).");
            return null;
        }

        // The date directories around the spawn instant. The YYYY/MM/DD level
        // follows codex's clock at file-creation time — scan the UTC date and
        // both neighbors to be immune to local-vs-UTC ambiguity and midnight.
        static IEnumerable<string> EnumerateRecentRollouts(string sessionsRoot, DateTime spawnedAtUtc)
        {
            for (var offset = -1; offset <= 1; offset++)
            {
                var d = spawnedAtUtc.Date.AddDays(offset);
                var dir = Path.Combine(sessionsRoot, $"{d:yyyy}", $"{d:MM}", $"{d:dd}");
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "rollout-*.jsonl"); }
                catch (IOException) { continue; }
                foreach (var f in files) yield return f;
            }
        }

        // First line of a rollout = {"type":"session_meta","payload":{"cwd":...}}.
        // Read ONLY that line — rollouts grow to MBs.
        static bool TryReadSessionMetaCwd(string file, out string cwd)
        {
            cwd = string.Empty;
            using var reader = new StreamReader(file);
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line)) return false;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "session_meta") return false;
            if (!root.TryGetProperty("payload", out var payload)) return false;
            if (!payload.TryGetProperty("cwd", out var cwdEl)) return false;
            var v = cwdEl.GetString();
            if (v == null) return false;
            cwd = v;
            return true;
        }

        static bool PathsEqual(string a, string b) =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
