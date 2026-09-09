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
    /// codex-cli 0.142.5 — see plans/codex-cli-support-design.md D14/D15/D16):
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
        // OpenAI's official standalone installer (first-listed in the codex
        // README, ahead of brew/npm). install.sh lands in ~/.local/bin;
        // install.ps1 lands in %LOCALAPPDATA%\Programs\OpenAI\Codex\bin.
        public string NativeInstallCommand =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? "irm https://chatgpt.com/codex/install.ps1 | iex"
                : "curl -fsSL https://chatgpt.com/codex/install.sh | sh";
        public IReadOnlyList<string> WellKnownBinaryPaths =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? new[]
                {
                    // install.ps1's default target (a junction onto its versioned
                    // store); it registers this dir on the REGISTRY user PATH only.
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".local", "bin", "codex.exe"),
                }
                : new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".local", "bin", "codex"),
                };
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

            // Approval-free but sandboxed: never prompt for shell commands
            // (failures return to the model), writes confined to the workspace
            // cwd + tmp + the user skill store (writable_roots below). The
            // panel's real tool path is MCP (unsandboxed, already
            // auto via default_tools_approval_mode below) — codex's own default
            // (on-request) would nag for every shell command, which defeats the
            // "just open the panel" goal. Deliberately NOT --dangerously-*: the
            // sandbox stays on.
            args.Add("-a");
            args.Add("never");
            args.Add("-s");
            args.Add("workspace-write");

            // The user skill store lives outside the workspace cwd, so the
            // sandbox denies the writes save-rhino-skill needs — including
            // through the workspace symlinks, which the sandbox resolves to
            // real paths before judging (spike-verified, codex sandbox
            // 0.143.0). Grant the store as an extra writable root. Forward
            // slashes: the -c value is parsed as TOML, where backslash
            // escapes proved fragile (spike: \\-escaped paths failed to
            // parse as an array and fell back to a literal string).
            args.Add("-c");
            args.Add("sandbox_workspace_write.writable_roots=[" +
                TomlString(Workspace.UserSkillsRoot.Replace('\\', '/')) + "]");

            // Network. Emitted in BOTH states rather than leaning on codex's
            // default, so the spawn's rollout turn_context says out loud which
            // one this tab got (diagnosing the OFF case from the transcript is
            // otherwise guesswork). This flag gates more than the internet:
            // codex's seatbelt profile hangs unix sockets AND the com.apple.*
            // mach services behind it — SecurityServer included, so with it OFF
            // the macOS Keychain is unreachable and every keychain-cached
            // credential reads as absent (measured, see D27).
            args.Add("-c");
            args.Add("sandbox_workspace_write.network_access=" +
                (ctx.CodexNetworkAccess ? "true" : "false"));

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

            // (3) This panel's rhino server. default_tools_approval_mode=approve
            // (NOT auto): approve = pre-approved, no prompt ever; auto = codex
            // decides and still prompts for risky tools like run_python
            // (spike-verified — with approve, tools/call goes straight through,
            // even in exec mode). Opening the panel is the consent for rhino
            // tools. The header tags the connection per agent (future host-side
            // attribution/lease hook).
            args.Add("-c");
            args.Add(
                $"mcp_servers.rhino={{url={TomlString($"http://localhost:{ctx.HostPort}/")}," +
                $"http_headers={{{TomlString("X-CoRhino-Agent")}={TomlString(ctx.AgentId)}}}," +
                $"default_tools_approval_mode={TomlString("approve")}}}");

            // The workspace is CoRhino's own dir; opening the panel is the
            // consent. Without this codex's first-run trust prompt blocks the
            // session (and answering it would write to the user's config.toml).
            args.Add("-c");
            args.Add($"projects.{TomlKeySegment(ctx.Cwd)}.trust_level={TomlString("trusted")}");

            // Status hooks (busy/waiting/idle → this agent's status file, watched
            // by the panel). Codex ships claude-style lifecycle hooks; the
            // command is the same cmd/sh polyglot echo claude uses, so no
            // per-OS variant is needed (unix runs sh -lc, Windows cmd.exe /C).
            // Non-managed hooks normally need one-time interactive trust; these
            // are CoRhino's own, so the bypass flag is the correct stance — the
            // user never wrote them and shouldn't be asked to vouch for them.
            // NOTE: requires a hooks-era codex (~0.142+); older builds fail to
            // launch on the unknown flag (pre-alpha: no fallback path).
            string HookRule(string state) =>
                $"[{{hooks=[{{type={TomlString("command")}," +
                $"command={TomlString(Workspace.StatusEchoCommand(state, ctx.HostPort, ctx.AgentId))}," +
                "timeout=10}]}]";
            args.Add("--dangerously-bypass-hook-trust");
            args.Add("-c");
            args.Add("hooks={" +
                $"UserPromptSubmit={HookRule("busy")}," +
                $"PreToolUse={HookRule("busy")}," +
                // Also the approval-granted edge: an approved tool always runs
                // and lands here, flipping waiting back to busy.
                $"PostToolUse={HookRule("busy")}," +
                $"PermissionRequest={HookRule("waiting")}," +
                $"Stop={HookRule("idle")}" +
                "}");

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
        // Session id discovery (D15)

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
