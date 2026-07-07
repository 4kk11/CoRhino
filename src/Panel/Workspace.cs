using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

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

        /// <summary>~/.corhino/session-names.json — user-given session names (owned by SessionNames).</summary>
        public static string SessionNamesPath => Path.Combine(BaseDir, "session-names.json");

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
        /// Ensure the shared workspace exists; seed its CLAUDE.md once (then
        /// user-owned). Returns the dir — the cwd of every claude session.
        /// </summary>
        public static string EnsureWorkspace()
        {
            var root = WorkspaceRoot;
            Directory.CreateDirectory(root);

            var claudeMd = Path.Combine(root, "CLAUDE.md");
            if (!File.Exists(claudeMd))
            {
                File.WriteAllText(claudeMd, ClaudeMdTemplate);
                Log.Write($"[CoRhino] Seeded CLAUDE.md at {claudeMd}");
            }

            return root;
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
        static string ClaudeMdTemplate => L10n.Japanese ? ClaudeMdTemplateJa : ClaudeMdTemplateEn;

        const string ClaudeMdTemplateJa = """
        # CoRhino Workspace

        この CLAUDE.md は CoRhino が起動するすべての Claude セッションが共有する作業ディレクトリです。
        セッションはアクティブな .3dm の場所ではなく常にこのディレクトリで起動します。Rhino の操作は
        `rhino` MCP サーバ（RhinoMCP）のツール経由で行ってください。接続先はパネルの Rhino インスタンス
        専用ポートのホストなので、ツール呼び出しは必ずそのパネルの Rhino に反映されます。

        ## 並列エージェントのルール（重要）

        同じ Rhino ドキュメントに **複数の Claude が同時接続**していることがあります。互いの作業を
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

        const string ClaudeMdTemplateEn = """
        # CoRhino Workspace

        This CLAUDE.md belongs to the working directory shared by every Claude
        session CoRhino starts. Sessions always launch here, not next to the
        active .3dm. Operate Rhino through the tools of the `rhino` MCP server
        (RhinoMCP); it points at the panel's Rhino instance on its dedicated
        port, so every tool call lands in that panel's Rhino.

        ## Parallel-agent rules (important)

        Multiple Claude sessions may be connected to the SAME Rhino document at
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
