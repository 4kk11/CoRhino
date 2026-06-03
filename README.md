# CoRhino

Rhino に **Claude Code をドックする** Rhino 8 プラグイン。サイドパネルに常駐した
Claude Code が、いま開いているその Rhino を操作できる。

非エンジニアでも「パネルを開くだけ」で使えることを目標にしており、`MCPConnect` /
設定ファイルの貼り付け / 手動の MCP 起動を一切踏ませない。Rhino 操作の実体（tools /
agents）は公式 [Rhino-MCP-Platform](https://github.com/mcneel/RhinoMCP) に乗り、
CoRhino はその**パネル UX・起動配線・オンボーディング**だけを担う（ツールは持たない）。

> [!WARNING]
> Pre-alpha / WIP。CLI・プロトコル・handler 名は予告なく変わる。

## 仕組み

```
Rhino プロセス
├── CoRhino プラグイン (.rhp, C#)
│   ├── "CoRhino" コマンド → サイドパネルを開く
│   └── パネル = WebView + xterm.js (タブ式) に PTY 経由の claude を表示
│        ├─ インスタンス専用ポートを採番 (base 10500)
│        ├─ RhinoMCP ホストを MCPSpawn でそのポートに起動 (1パネル=1ホスト)
│        └─ タブごと (= エージェント) に claude を並列起動
│             ├─ ~/.corhino/sessions/<port>/<agentId>/ を用意 (CLAUDE.md + .mcp.json)
│             └─ 全タブが同じ http://localhost:<port>/ に接続 (同じ doc を操作)
└── Rhino-MCP-Platform プラグイン (.rhp)
     └── http://localhost:<port>/ で MCP ホストを提供
```

- **direct-HTTP 接続**: claude は `http://localhost:<port>/` の `rhino` MCP サーバに
  直接つながる。node router は使わない（パネルの意味論は「ドックされた“この”Rhino を操作」）。
- **インスタンス単位のポート**: パネルごとに空きポートを採番するので、複数ドキュメントでも
  各パネルが自分の Rhino を操作する。
- **1 ドキュメントに複数 claude（並列エージェント）**: 1パネル内のタブを「＋」で増やすと、
  複数の claude が同じ Rhino を並列に操作する。RhinoMCP ホストはステートレスでツール実行を
  UI スレッドで直列化するため安全。各エージェントには専用レイヤ（`agent-1/` …）が割り当てられ、
  書き込みが衝突しないようになっている。
- **セッション隔離**: claude は `--strict-mcp-config --mcp-config <session>/.mcp.json` で
  起動し、ユーザーのグローバル/プロジェクト MCP 設定を読み込まない。`~/.claude` を汚さない。
- **MCP ホスト起動**: `RHINO_MCP_AUTOSTART_PORT` をセットして hidden コマンド `_-MCPSpawn`
  を実行する（`MCPStart` の対話的ポート入力は runscript と相性が悪いため使わない）。

## 必要環境

- **Rhino 8**（macOS: Apple Silicon / Intel、Windows）
- **[Claude Code](https://claude.com/claude-code) CLI** (`claude`) が PATH 上にあること
  - 見つからない場合はパネルがログインシェル (`bash -l` / `cmd.exe`) にフォールバックする
- **Rhino-MCP-Platform**（yak パッケージ）
  - 未導入ならパネルが検出し、同意ダイアログ → `yak install` → 再起動案内まで自動で行う

## ビルド

UI（vite）のビルドは .NET ビルドに統合済み。**`dotnet build` 一発**で完結する。

```bash
dotnet build src/CoRhino.csproj
```

- `BuildUi` ターゲットが `ui/` で `npm install`（初回のみ）+ `npm run build` を実行
- ビルド成果物を `web/` として `.rhp` の隣に配置し、PostBuild が Rhino の
  plugin ディレクトリ（macOS: `~/Library/Application Support/McNeel/Rhinoceros/8.0/MacPlugIns/CoRhino`）へコピー
- macOS は arm64/x64 の native dylib を `lipo` でユニバーサル化して同梱

> コピー先（plugin ディレクトリ）を直接編集しないこと。常に再ビルドで反映する。

## 使い方

1. プラグインをビルド（上記）して Rhino を起動／再起動する
2. Rhino のコマンドラインで `CoRhino` を実行 → サイドパネルが開く
3. 初回、Rhino-MCP-Platform が無ければ導入の同意ダイアログが出る → インストール後に Rhino 再起動
4. パネル内の Claude Code に「今のドキュメントに何がある？」のように指示する

### 並列エージェント（タブ）

タブバーの「＋」で **同じ Rhino ドキュメントに対して claude をもう1つ**起動できる。各タブは
独立した Claude セッション（別 cwd・別履歴）で、専用レイヤ接頭辞（`agent-1/`, `agent-2/` …）が
割り当てられる。各エージェントは自分のレイヤ配下にだけ書き込み、他レイヤは読み取りのみとする
規約で動くので、並列で別々の作業（例: 片方がジオメトリ生成、片方が解析）を進めても衝突しにくい。
タブの「×」でそのエージェントだけ終了できる。

> 注意: レイヤ分離は CLAUDE.md による**規約**であり、ホストが強制するわけではない。同じオブジェクトを
> 跨いで触ると論理衝突し得る。また各エージェントは互いの変更をリアルタイムには知らない（最新状態は
> 都度問い合わせる）。

### セッションの終了と再起動

パネル内で `/exit`（または `Ctrl-D` 等）で Claude Code を終了すると、そのタブの PTY とプロセスが
後始末され、タブが閉じる。**最後のタブが終了するとパネルも自動で閉じる**。もう一度 `CoRhino`
コマンドを実行すると、パネルが再び開き、**新しい Claude セッションが起動する**（このインスタンスの
MCP ホスト・ポートはそのまま再利用される）。セッションが生きている間に `CoRhino` を実行した
場合はパネルが前面に来るだけ。

## プロジェクト構成

```
src/                        C# Rhino プラグイン
├── CoRhinoPlugin.cs        プラグイン本体（起動時ロード、パネル登録）
├── Commands/               "CoRhino" コマンド（パネルを開く）
├── Panel/
│   ├── CoRhinoPanel.cs     WebView + xterm.js (タブ式)、ポート採番、複数 claude 起動、MCP ホスト起動
│   ├── Workspace.cs        ~/.corhino/sessions/<port>/<agentId>/ と .mcp.json / CLAUDE.md の seed
│   └── RhinoMcpInstaller.cs  Rhino-MCP-Platform の yak オンボーディング
├── Pty/                    PTY セッション（Porta.Pty）と claude CLI 解決
└── Resources/icon.png      パネルアイコン
ui/                         パネル UI（TypeScript + xterm.js, vite）
docs/                       設計・アーキテクチャ・ロードマップ（下記）
```

## ドキュメント

役割を1ファイル1責務で分けている。

| ファイル | 責務（何が書いてあるか） |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | **現在の構造**（how）。コンポーネント・データフロー・ライフサイクル・設定リファレンス |
| [`docs/details/io-flow-xterm-pty.md`](docs/details/io-flow-xterm-pty.md) | xterm ⇄ PTY の入出力フローのコード単位の深掘り（`docs/details/` は深掘り資料置き場） |
| [`docs/design-history.md`](docs/design-history.md) | **なぜこの形か**（why）。設計判断・検証ログ・直したバグ |
| [`docs/roadmap.md`](docs/roadmap.md) | **これから**（未来）。未実装・今後の作業 |

## 開発

UI を vite dev サーバから読ませる場合は、Rhino 起動前に環境変数を設定する:

```bash
export CORHINO_WEBUI_URL=http://localhost:5173   # ui/ で `npm run dev` 中
```

設定時はパネルがディスク上の `web/index.html` ではなく dev サーバの URL を読む。

各ドキュメントの詳細は上記「ドキュメント」を参照。
