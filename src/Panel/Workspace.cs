using System;
using System.IO;

namespace RhinoClaudePanel.Panel
{
    /// <summary>
    /// Owns the single, fixed directory that the embedded Claude session always
    /// runs in. The session no longer follows the active .3dm; instead it always
    /// launches in <see cref="Root"/> so that one CLAUDE.md and one .mcp.json
    /// (RhinoMCP only) drive every session regardless of which model is open.
    /// </summary>
    static class Workspace
    {
        /// <summary>
        /// Port the panel starts the in-process RhinoMCP HTTP server on
        /// (MCPStart), and that the generated .mcp.json points claude at.
        /// </summary>
        public const int HostPort = 10500;

        /// <summary>~/.rhino-claude-panel/workspace</summary>
        public static string Root
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".rhino-claude-panel", "workspace");
            }
        }

        public static string McpConfigPath => Path.Combine(Root, ".mcp.json");

        static string ClaudeMdPath => Path.Combine(Root, "CLAUDE.md");

        /// <summary>
        /// Create the workspace directory and seed CLAUDE.md / .mcp.json from the
        /// bundled templates. Existing files are never overwritten — the user owns
        /// them once created. Returns the workspace root path.
        /// </summary>
        public static string EnsureInitialized()
        {
            var root = Root;
            Directory.CreateDirectory(root);

            if (!File.Exists(ClaudeMdPath))
            {
                File.WriteAllText(ClaudeMdPath, ClaudeMdTemplate);
                Log.Write($"[Claude] Seeded CLAUDE.md at {ClaudeMdPath}");
            }

            if (!File.Exists(McpConfigPath))
            {
                File.WriteAllText(McpConfigPath, McpConfigTemplate());
                Log.Write($"[Claude] Seeded .mcp.json at {McpConfigPath}");
            }

            return root;
        }

        static string McpConfigTemplate()
        {
            // Single MCP server: the RhinoMCP HTTP host running *inside the docked
            // Rhino* (started by the panel via MCPStart). Direct-HTTP — no node
            // router, no separately-spawned Rhino — so claude operates THIS Rhino.
            // Generated once; edit this file (not the template) to tweak it.
            return $$"""
            {
              "mcpServers": {
                "rhino": {
                  "type": "http",
                  "url": "http://localhost:{{HostPort}}/"
                }
              }
            }

            """;
        }

        const string ClaudeMdTemplate = """
        # Rhino Claude Workspace

        この CLAUDE.md は RhinoClaudePanel が起動する Claude セッション専用の作業ディレクトリです。
        セッションはアクティブな .3dm の場所ではなく、常にこのディレクトリで起動します。
        Rhino の操作は `rhino` MCP サーバ（RhinoMCP）のツール経由で行ってください。

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
