using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// Owns the files the embedded Claude sessions run against. ALL sessions of
    /// all tabs and panels share ONE cwd (~/.corhino/workspace): claude keys its
    /// transcript store (~/.claude/projects/&lt;cwd-slug&gt;) by cwd and --resume only
    /// works from the same cwd, so a single stable dir means any past session is
    /// resumable from any tab at any time — no per-slot dirs, no port in the
    /// path, no "which dir does this session belong to" bookkeeping. What USED to
    /// be per-dir is delivered per-spawn instead: the RhinoMCP config goes to
    /// ~/.corhino/runtime/ (keyed by port+agentId — --mcp-config takes an
    /// explicit path, it never needed to live in the cwd), and the parallel-agent
    /// layer prefix is injected via --append-system-prompt (derived
    /// deterministically from the sessionId, so a resumed session gets the same
    /// layer it always had). The shared CLAUDE.md is seeded once and then
    /// user-owned.
    /// </summary>
    static class Workspace
    {
        /// <summary>Lowest port to probe for a per-instance RhinoMCP host.</summary>
        public const int BasePort = 10500;

        static string BaseDir
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".corhino");
            }
        }

        /// <summary>~/.corhino/workspace — the single shared cwd of every claude session (see class doc).</summary>
        public static string WorkspaceRoot => Path.Combine(BaseDir, "workspace");

        /// <summary>~/.corhino/runtime — per-spawn MCP configs (ephemeral, derived, safe to delete).</summary>
        static string RuntimeRoot => Path.Combine(BaseDir, "runtime");

        /// <summary>
        /// ~/.corhino/runtime/status — one file per live agent, written by hooks
        /// injected into the CLI (claude via --settings, codex via -c hooks) and
        /// watched by the panel to drive the tab/session status indicators.
        /// </summary>
        public static string StatusDir => Path.Combine(RuntimeRoot, "status");

        /// <summary>~/.corhino/session-names.json — user-given session names (owned by SessionNames).</summary>
        public static string SessionNamesPath => Path.Combine(BaseDir, "session-names.json");

        /// <summary>~/.corhino/session-layers.json — codex sessionId → layer token map (owned by SessionLayers).</summary>
        public static string SessionLayersPath => Path.Combine(BaseDir, "session-layers.json");

        /// <summary>~/.corhino/skills — the user's personal skill plugin root.</summary>
        public static string UserSkillsRoot => Path.Combine(BaseDir, "skills");

        /// <summary>
        /// Find a free TCP port for this Rhino's MCP host, starting at
        /// <see cref="BasePort"/>. When Rhino A already holds 10500, Rhino B binds
        /// 10501, and so on — each instance ends up with a distinct port and never
        /// steals another's endpoint.
        /// </summary>
        public static int ResolveFreePort(int count = 100)
        {
            for (var port = BasePort; port < BasePort + count; port++)
            {
                if (IsPortFree(port)) return port;
            }
            return BasePort; // give up after the range; MCPStart will surface the bind error
        }

        static bool IsPortFree(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>
        /// Ensure the shared workspace exists; seed its guide files once (then
        /// user-owned). Returns the dir — the cwd of every agent session.
        /// CLAUDE.md and AGENTS.md carry the SAME content from one template:
        /// claude reads the former, codex the latter. They are seeded as separate
        /// files (not a symlink) so the user can grow them apart per CLI.
        /// </summary>
        public static string EnsureWorkspace()
        {
            var root = WorkspaceRoot;
            Directory.CreateDirectory(root);

            foreach (var name in new[] { "CLAUDE.md", "AGENTS.md" })
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, WorkspaceGuideTemplate);
                    Log.Write($"[CoRhino] Seeded {name} at {path}");
                }
            }

            EnsureCodexSkillsLinks();

            return root;
        }

        // Serializes the check-then-create below (EnsureWorkspace runs per spawn
        // on background threads — two concurrent spawns must not both try to
        // create a link) and owns the log-once failure set.
        static readonly object CodexSkillsLinkGate = new();
        static readonly HashSet<string> FailedLinks = new(StringComparer.Ordinal);

        /// <summary>&lt;workspace&gt;/.agents/skills — codex project-skills root, linked onto the user store.</summary>
        static string CodexUserSkillsLinkPath => Path.Combine(WorkspaceRoot, ".agents", "skills");

        /// <summary>&lt;workspace&gt;/.codex/skills — codex's second project-skills root, linked onto the bundled cc-plugin skills.</summary>
        static string CodexBundledSkillsLinkPath => Path.Combine(WorkspaceRoot, ".codex", "skills");

        /// <summary>The bundled cc-plugin's skills dir next to the assembly (what claude mounts via --plugin-dir).</summary>
        static string BundledSkillsDir => Path.Combine(
            Path.GetDirectoryName(typeof(Workspace).Assembly.Location) ?? string.Empty,
            "cc-plugin", "skills");

        /// <summary>
        /// The codex side of the skills wiring. Codex has no --plugin-dir
        /// equivalent, but it discovers project skills at BOTH
        /// &lt;cwd&gt;/.agents/skills and &lt;cwd&gt;/.codex/skills (spike-verified,
        /// 0.142.5, symlinks included) — and the cwd is CoRhino's own workspace.
        /// Two symlinks put the same skills claude mounts into codex sessions
        /// without touching ~/.codex: the user store on the .agents root, the
        /// bundled cc-plugin skills on the .codex root. Existing entries — the
        /// user's own dirs or links — are left alone.
        /// </summary>
        static void EnsureCodexSkillsLinks()
        {
            EnsureLink(CodexUserSkillsLinkPath, Path.Combine(EnsureUserSkillsPlugin(), "skills"), "codex user skills");
            // Parity with claude's bundled --plugin-dir: skip silently when a
            // stripped install lacks cc-plugin.
            if (Directory.Exists(BundledSkillsDir))
                EnsureLink(CodexBundledSkillsLinkPath, BundledSkillsDir, "codex bundled skills");
        }

        static void EnsureLink(string link, string target, string label)
        {
            lock (CodexSkillsLinkGate)
            {
                try
                {
                    if (Directory.Exists(link) || File.Exists(link)) return; // present (or user-owned) — done
                    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                    Directory.CreateSymbolicLink(link, target);
                    Log.Write($"[CoRhino] Linked {link} -> {target} ({label}).");
                }
                catch (Exception ex)
                {
                    // The entry appearing between check and create (another Rhino
                    // process, the user) is success, not failure.
                    if (Directory.Exists(link) || File.Exists(link)) return;
                    if (!FailedLinks.Add(link)) return; // log once per link
                    // Codex tabs just run without these skills; claude is unaffected.
                    // Windows needs Developer Mode (or elevation) for symlinks — the
                    // usual cause here.
                    Log.Write($"[CoRhino] {label} link failed (codex tabs run without them): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>Whether codex sessions actually mount the user skill store (see IsOurLink).</summary>
        public static bool CodexUserSkillsLinkActive() =>
            IsOurLink(CodexUserSkillsLinkPath, Path.Combine(UserSkillsRoot, "skills"));

        /// <summary>Whether codex sessions actually mount the bundled cc-plugin skills (see IsOurLink).</summary>
        public static bool CodexBundledSkillsLinkActive() =>
            IsOurLink(CodexBundledSkillsLinkPath, BundledSkillsDir);

        // True only when the entry is OUR symlink onto the expected target —
        // i.e. codex sessions really load what the drawer would list. False
        // when creation failed, or when the user replaced the entry with their
        // own dir/link (codex then loads THAT, which CoRhino doesn't manage).
        static bool IsOurLink(string link, string expectedTarget)
        {
            try
            {
                var info = new DirectoryInfo(link);
                if (!info.Exists || info.LinkTarget == null) return false;
                var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(info.LinkTarget));
                var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedTarget));
                return string.Equals(target, expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Write this spawn's MCP config and return its path (passed to claude via
        /// --mcp-config). Keyed by port+agentId: the port is unique per panel, so
        /// two panels' agent-1 tabs never share a file. Derived content — always
        /// rewritten; claude reads it once at startup.
        /// </summary>
        public static string EnsureAgentMcpConfig(int port, string agentId)
        {
            Directory.CreateDirectory(RuntimeRoot);
            var path = Path.Combine(RuntimeRoot, $"mcp-{port}-{agentId}.json");
            File.WriteAllText(path, McpConfigTemplate(port, agentId));
            Log.Write($"[CoRhino] Wrote MCP config at {path} (port {port}, agent {agentId})");
            return path;
        }

        /// <summary>
        /// This agent's status file, forward-slash normalized. The path is baked
        /// into the hook commands at spawn time AND parsed back from watcher
        /// events, so both sides must build it here. Keyed by port+agentId like
        /// the MCP config: unique per panel, no session-id parsing needed.
        /// </summary>
        public static string AgentStatusFilePath(int port, string agentId) =>
            Path.Combine(StatusDir, $"{port}-{agentId}").Replace('\\', '/');

        /// <summary>
        /// The hook command that records a status transition. Deliberately a
        /// polyglot that means the same thing under cmd.exe AND sh: claude does
        /// not document which shell runs hook commands on Windows, and codex uses
        /// cmd.exe /C there — one string covers every combination. No space
        /// before '&gt;' (cmd would include it in the payload; readers Trim()
        /// anyway), quoted forward-slash absolute path (both shells accept it).
        /// </summary>
        public static string StatusEchoCommand(string state, int port, string agentId) =>
            $"echo {state}>\"{AgentStatusFilePath(port, agentId)}\"";

        /// <summary>
        /// Reset this agent's status file to idle before spawn, so a tab never
        /// shows a stale state from a previous session that used the same
        /// port+agentId key.
        /// </summary>
        public static void InitAgentStatusFile(int port, string agentId)
        {
            Directory.CreateDirectory(StatusDir);
            File.WriteAllText(AgentStatusFilePath(port, agentId), "idle");
        }

        /// <summary>Remove this agent's status file when its tab goes away (best-effort).</summary>
        public static void DeleteAgentStatusFile(int port, string agentId)
        {
            try { File.Delete(AgentStatusFilePath(port, agentId)); }
            catch (Exception ex) { Log.Write($"[CoRhino] status file delete failed ({agentId}): {ex.Message}"); }
        }

        /// <summary>
        /// Write this spawn's status-hook settings and return its path (passed to
        /// claude via --settings — hooks delivered this way need no interactive
        /// trust approval and leave ~/.claude untouched, same stance as
        /// --mcp-config). The hooks map claude's lifecycle onto the three-state
        /// model the panel displays: prompt/tool activity → busy, a permission
        /// prompt → waiting, response finished → idle. Stop does NOT fire on an
        /// Esc interrupt (documented behavior) — the panel compensates by
        /// watching stdin for a lone ESC.
        /// </summary>
        public static string EnsureClaudeStatusSettings(int port, string agentId)
        {
            Directory.CreateDirectory(StatusDir);
            var path = Path.Combine(RuntimeRoot, $"status-hooks-{port}-{agentId}.json");

            object[] Rule(string state, string? matcher = null)
            {
                var rule = new Dictionary<string, object>();
                if (matcher != null) rule["matcher"] = matcher;
                // timeout is a hang backstop only; the echo is effectively instant.
                rule["hooks"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "command",
                        ["command"] = StatusEchoCommand(state, port, agentId),
                        ["timeout"] = 10,
                    },
                };
                return new object[] { rule };
            }

            var settings = new Dictionary<string, object>
            {
                ["hooks"] = new Dictionary<string, object>
                {
                    ["UserPromptSubmit"] = Rule("busy"),
                    ["PreToolUse"] = Rule("busy"),
                    // Also the "approval granted" edge: the permission dialog has
                    // no answered-event, but an approved tool always runs and
                    // lands here, flipping waiting back to busy.
                    ["PostToolUse"] = Rule("busy"),
                    ["Notification"] = Rule("waiting", "permission_prompt"),
                    ["Stop"] = Rule("idle"),
                    ["SessionEnd"] = Rule("idle"),
                },
            };

            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        /// <summary>
        /// Ensure the user's personal skill plugin exists and return its root, which
        /// StartSession passes to claude via <c>--plugin-dir</c>. Skills the user drops
        /// (or has an agent write) under <c>skills/</c> ride into every session of every
        /// panel without touching <c>~/.claude</c> — the same isolation stance as the
        /// MCP config. CoRhino only owns this wiring; the skill content is the user's
        /// (the design boundary keeps Rhino know-how out of this repo). The manifest and
        /// README are seeded once and then user-owned, like CLAUDE.md in EnsureSession.
        /// </summary>
        public static string EnsureUserSkillsPlugin()
        {
            var root = UserSkillsRoot;
            Directory.CreateDirectory(Path.Combine(root, "skills"));

            var manifest = Path.Combine(root, ".claude-plugin", "plugin.json");
            if (!File.Exists(manifest))
            {
                Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
                File.WriteAllText(manifest, UserSkillsManifestTemplate);
                Log.Write($"[CoRhino] Seeded user skills plugin manifest at {manifest}");
            }

            var readme = Path.Combine(root, "README.md");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme, UserSkillsReadmeTemplate);
            }

            return root;
        }

        static string McpConfigTemplate(int port, string agentId)
        {
            // Single MCP server: the RhinoMCP HTTP host running *inside this* Rhino
            // on this panel's port. Direct-HTTP — no node router — so claude operates
            // the Rhino its panel is docked in. Every agent (tab) on this panel points
            // at the SAME port: the host is stateless and serializes tool calls on the
            // UI thread, so parallel agents share one document safely. The
            // X-CoRhino-Agent header tags each connection so the host can later
            // attribute changes or hand out per-agent leases (not used yet).
            return $$"""
            {
              "mcpServers": {
                "rhino": {
                  "type": "http",
                  "url": "http://localhost:{{port}}/",
                  "headers": {
                    "X-CoRhino-Agent": "{{agentId}}"
                  }
                }
              }
            }

            """;
        }

        // Seed-time language (L10n follows Rhino's UI language): the file is
        // user-owned after the first seed, so it keeps the language it was born
        // with even if Rhino's language later changes — delete it to re-seed.
        static string WorkspaceGuideTemplate => L10n.Japanese ? WorkspaceGuideTemplateJa : WorkspaceGuideTemplateEn;

        const string WorkspaceGuideTemplateJa = """
        # CoRhino Workspace

        この文書は CoRhino が起動するすべてのエージェントセッション（Claude Code / Codex）が共有する
        作業ディレクトリのガイドです。セッションはアクティブな .3dm の場所ではなく常にこのディレクトリで
        起動します。Rhino の操作は
        `rhino` MCP サーバ（RhinoMCP）のツール経由で行ってください。接続先はパネルの Rhino インスタンス
        専用ポートのホストなので、ツール呼び出しは必ずそのパネルの Rhino に反映されます。

        ## 並列エージェントのルール（重要）

        同じ Rhino ドキュメントに **複数のエージェントが同時接続**していることがあります。互いの作業を
        壊さないため:

        - あなたの担当レイヤは **システムプロンプトで `s-XXXXXXXX` の形式で指示**されます。
          新規作成・移動・変更・削除は**担当レイヤ配下のオブジェクトのみ**に行うこと。
        - オブジェクト作成時は必ず現在レイヤを担当レイヤ（または `担当レイヤ/<用途>`）に
          設定してから作る。
        - 他レイヤは **読み取りは自由**だが、**書き込み・削除はしない**。
        - 他エージェントの変更はリアルタイムには通知されない。最新状態が要るときは
          `list_objects` 等で都度問い合わせること。

        ## 主要ツール（RhinoMCP）

        - `run_command` — Rhino のコマンドを実行（コマンドライン操作のエントリ）
        - `run_python` — RhinoCommon を Python で実行。汎用エスケープハッチ
        - `run_csharp` — RhinoCommon を C# で実行
        - `list_objects` — ドキュメント内オブジェクトの列挙
        - `get_viewport_image` — ビューポートを画像取得して確認
        - `open_doc` / `save_doc` — モデルを開く / 保存する
        - `spawn_slot` / `list_slots` / `close_slot` — メイン UI を汚さない headless スロットで一括処理

        ## 進め方

        - 形状追加・編集・問い合わせは原則 `run_python` で書く。専用ラッパーがある操作だけ専用ツールを使う
        - 大量ファイル処理は `spawn_slot` でスロットを起こし、その中で open/run/save/close する
        - 変更後は `get_viewport_image` か `list_objects` で結果を必ず確認する

        ## メモ

        - 生成物・スクラッチはこのディレクトリ配下に置く（全セッション共有なので、まとまった作業は
          サブディレクトリを切ると衝突しない）
        """;

        const string WorkspaceGuideTemplateEn = """
        # CoRhino Workspace

        This guide belongs to the working directory shared by every agent
        session CoRhino starts (Claude Code / Codex). Sessions always launch
        here, not next to the active .3dm. Operate Rhino through the tools of the `rhino` MCP server
        (RhinoMCP); it points at the panel's Rhino instance on its dedicated
        port, so every tool call lands in that panel's Rhino.

        ## Parallel-agent rules (important)

        Multiple agent sessions may be connected to the SAME Rhino document at
        once. To keep from clobbering each other:

        - Your assigned layer is given in the system prompt in the form
          `s-XXXXXXXX`. Create, move, modify, and delete ONLY objects under
          your assigned layer.
        - Before creating objects, set the current layer to your assigned layer
          (or `assigned-layer/<purpose>`).
        - Other layers are free to READ, but never write to or delete from them.
        - Other agents' changes are not pushed to you. When you need fresh
          state, query it (e.g. `list_objects`).

        ## Key tools (RhinoMCP)

        - `run_command` — run a Rhino command (the command-line entry point)
        - `run_python` — run RhinoCommon via Python; the general escape hatch
        - `run_csharp` — run RhinoCommon via C#
        - `list_objects` — enumerate objects in the document
        - `get_viewport_image` — capture the viewport to verify results
        - `open_doc` / `save_doc` — open / save models
        - `spawn_slot` / `list_slots` / `close_slot` — batch work in headless slots that don't touch the main UI

        ## How to work

        - Prefer `run_python` for creating/editing/querying geometry; use the dedicated wrappers only where they exist
        - For bulk file processing, spawn a slot and open/run/save/close inside it
        - After changes, always verify with `get_viewport_image` or `list_objects`

        ## Notes

        - Put generated files and scratch under this directory (it is shared by
          all sessions — use a subdirectory for larger jobs to avoid collisions)
        """;

        static string UserSkillsManifestTemplate => L10n.Japanese ? UserSkillsManifestTemplateJa : UserSkillsManifestTemplateEn;

        const string UserSkillsManifestTemplateJa = """
        {
          "name": "my-rhino-skills",
          "description": "CoRhino の全セッションに読み込まれる自作スキル集",
          "version": "0.1.0"
        }

        """;

        const string UserSkillsManifestTemplateEn = """
        {
          "name": "my-rhino-skills",
          "description": "Personal skills loaded into every CoRhino session",
          "version": "0.1.0"
        }

        """;

        static string UserSkillsReadmeTemplate => L10n.Japanese ? UserSkillsReadmeTemplateJa : UserSkillsReadmeTemplateEn;

        const string UserSkillsReadmeTemplateJa = """
        # My Rhino Skills

        CoRhino が起動するすべての Claude セッション（全パネル・全タブ）に読み込まれる、
        あなた専用のスキル置き場。ここに置いたスキルは `~/.claude` に触れずに CoRhino の
        セッションだけへ届く。

        ## スキルの追加

        `skills/<skill-name>/SKILL.md` を作る:

        ```markdown
        ---
        name: facade-louver
        description: ファサードのルーバーを生成するとき。ピッチ・角度・基準サーフェスを確認してから run_python で生成する
        ---

        ここに手順・ノウハウ・コード断片を書く。
        ```

        - `description` が発動条件。Claude はこれを見てスキルを使うか判断するので、
          「いつ使うか」を具体的に書く。
        - 手で書かなくても、パネル内の Claude に「今の手順をスキルにして」と頼めば、
          同梱の `save-rhino-skill` メタスキルがこの規約どおりに書き出してくれる。

        ## 反映タイミング

        - 新しいタブ / セッションには自動で載る。
        - 実行中のセッションには `/plugin reload-plugins` で反映する。
        """;

        const string UserSkillsReadmeTemplateEn = """
        # My Rhino Skills

        Your personal skill collection, loaded into every Claude session CoRhino
        starts (all panels, all tabs). Skills placed here reach CoRhino sessions
        only — nothing under `~/.claude` is touched.

        ## Adding a skill

        Create `skills/<skill-name>/SKILL.md`:

        ```markdown
        ---
        name: facade-louver
        description: When generating facade louvers. Confirm pitch, angle, and the base surface, then generate with run_python
        ---

        Write the steps, know-how, and code snippets here.
        ```

        - `description` is the trigger. Claude reads it to decide whether to use
          the skill, so state concretely WHEN it applies.
        - You don't have to write these by hand: ask the Claude in the panel to
          "turn this workflow into a skill" and the bundled `save-rhino-skill`
          meta-skill writes it out in this format.

        ## When skills take effect

        - New tabs / sessions pick them up automatically.
        - Running sessions need `/plugin reload-plugins`.
        """;
    }
}
