using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace RhinoClaudePanel.Panel
{
    /// <summary>
    /// Owns the per-Rhino-instance working directory the embedded Claude session
    /// runs in. The session does not follow the active .3dm; instead each panel
    /// gets a stable session dir keyed by the host MCP port it owns, so two Rhinos
    /// running side by side never share a cwd, a claude history, or — critically —
    /// an MCP endpoint. Each session dir carries its own CLAUDE.md (from the shared
    /// template) and a .mcp.json pointing at *that* Rhino's host.
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
                return Path.Combine(home, ".rhino-claude-panel");
            }
        }

        /// <summary>~/.rhino-claude-panel/sessions/&lt;port&gt; — this instance's cwd.</summary>
        public static string SessionRoot(int port) => Path.Combine(BaseDir, "sessions", port.ToString());

        public static string McpConfigPath(int port) => Path.Combine(SessionRoot(port), ".mcp.json");

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
        /// Create this instance's session dir and (re)write its files. CLAUDE.md is
        /// seeded once from the shared template and then owned by the user. The
        /// .mcp.json is derived purely from <paramref name="port"/>, so it is always
        /// rewritten to stay correct. Returns the session dir (the claude cwd).
        /// </summary>
        public static string EnsureSession(int port)
        {
            var root = SessionRoot(port);
            Directory.CreateDirectory(root);

            var claudeMd = Path.Combine(root, "CLAUDE.md");
            if (!File.Exists(claudeMd))
            {
                File.WriteAllText(claudeMd, ClaudeMdTemplate);
                Log.Write($"[Claude] Seeded CLAUDE.md at {claudeMd}");
            }

            File.WriteAllText(McpConfigPath(port), McpConfigTemplate(port));
            Log.Write($"[Claude] Wrote .mcp.json at {McpConfigPath(port)} (port {port})");

            return root;
        }

        static string McpConfigTemplate(int port)
        {
            // Single MCP server: the RhinoMCP HTTP host running *inside this* Rhino
            // (started by the panel via MCPStart on this instance's port). Direct-HTTP
            // — no node router, no separately-spawned Rhino — so claude operates the
            // Rhino its panel is docked in, not whichever instance grabbed 10500.
            return $$"""
            {
              "mcpServers": {
                "rhino": {
                  "type": "http",
                  "url": "http://localhost:{{port}}/"
                }
              }
            }

            """;
        }

        const string ClaudeMdTemplate = """
        # Rhino Claude Workspace

        この CLAUDE.md は RhinoClaudePanel が起動する Claude セッション専用の作業ディレクトリです。
        このディレクトリはパネルがドックされている Rhino インスタンス1つに対応し、セッションは
        アクティブな .3dm の場所ではなく常にこのディレクトリで起動します。Rhino の操作は `rhino`
        MCP サーバ（RhinoMCP）のツール経由で行ってください。接続先はこのインスタンス専用ポートの
        ホストなので、ツール呼び出しは必ずこのパネルの Rhino に反映されます。

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
    }
}
