using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace CoRhino.Panel
{
    /// <summary>
    /// Owns the per-agent working directories the embedded Claude sessions run in.
    /// A panel hosts one RhinoMCP port (its Rhino instance) but can run several
    /// claude agents in parallel against it — one per tab. Each agent gets its own
    /// session dir under that port, so agents never share a cwd or a claude history,
    /// yet all point their .mcp.json at the *same* host port (the host is stateless
    /// and serializes tool calls on the UI thread, so multiple agents safely operate
    /// the one document). Each agent's .mcp.json carries a distinct
    /// <c>X-CoRhino-Agent</c> header for future host-side attribution, and its
    /// CLAUDE.md is seeded with a private layer prefix so parallel agents write into
    /// disjoint layers and don't clobber each other.
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

        /// <summary>~/.corhino/sessions/&lt;port&gt; — this panel's (Rhino instance's) root.</summary>
        public static string PortRoot(int port) => Path.Combine(BaseDir, "sessions", port.ToString());

        /// <summary>~/.corhino/sessions/&lt;port&gt;/&lt;agentId&gt; — one agent's cwd.</summary>
        public static string AgentSessionRoot(int port, string agentId) => Path.Combine(PortRoot(port), agentId);

        public static string McpConfigPath(int port, string agentId) => Path.Combine(AgentSessionRoot(port, agentId), ".mcp.json");

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
        /// Create this agent's session dir and (re)write its files. CLAUDE.md is
        /// seeded once (with the agent's private layer prefix baked in) and then
        /// owned by the user. The .mcp.json is derived purely from
        /// <paramref name="port"/> and <paramref name="agentId"/>, so it is always
        /// rewritten to stay correct. Returns the session dir (the claude cwd).
        /// </summary>
        public static string EnsureSession(int port, string agentId, string layerPrefix)
        {
            var root = AgentSessionRoot(port, agentId);
            Directory.CreateDirectory(root);

            var claudeMd = Path.Combine(root, "CLAUDE.md");
            if (!File.Exists(claudeMd))
            {
                File.WriteAllText(claudeMd, ClaudeMdTemplate(layerPrefix));
                Log.Write($"[CoRhino] Seeded CLAUDE.md at {claudeMd} (layer {layerPrefix})");
            }

            File.WriteAllText(McpConfigPath(port, agentId), McpConfigTemplate(port, agentId));
            Log.Write($"[CoRhino] Wrote .mcp.json at {McpConfigPath(port, agentId)} (port {port}, agent {agentId})");

            return root;
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

        static string ClaudeMdTemplate(string layerPrefix) => $$"""
        # CoRhino Workspace — {{layerPrefix}}

        この CLAUDE.md は CoRhino が起動する Claude セッション（エージェント `{{layerPrefix}}`）専用の
        作業ディレクトリです。このパネルがドックされている Rhino インスタンス1つに対応し、セッションは
        アクティブな .3dm の場所ではなく常にこのディレクトリで起動します。Rhino の操作は `rhino`
        MCP サーバ（RhinoMCP）のツール経由で行ってください。接続先はこのインスタンス専用ポートの
        ホストなので、ツール呼び出しは必ずこのパネルの Rhino に反映されます。

        ## 並列エージェントのルール（重要）

        同じ Rhino ドキュメントに **複数の Claude が同時接続**しています。互いの作業を壊さないため:

        - あなたの担当レイヤは **`{{layerPrefix}}`**。新規作成・移動・変更・削除は
          **`{{layerPrefix}}` 配下のレイヤにあるオブジェクトのみ**に行うこと。
        - オブジェクト作成時は必ず現在レイヤを `{{layerPrefix}}`（または `{{layerPrefix}}/<用途>`）に
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

        - 生成物・スクラッチはこのディレクトリ配下に置く
        """;

        const string UserSkillsManifestTemplate = """
        {
          "name": "my-rhino-skills",
          "description": "CoRhino の全セッションに読み込まれる自作スキル集",
          "version": "0.1.0"
        }

        """;

        const string UserSkillsReadmeTemplate = """
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
    }
}
