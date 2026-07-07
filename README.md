# CoRhino

Rhino に **AI コーディングエージェントをドックする** Rhino 8 プラグイン。サイドパネルに常駐した
エージェント（Claude Code / Codex CLI、タブごとに選択）が、いま開いているその Rhino を操作できる。

非エンジニアでも「パネルを開くだけ」で使えることを目標にしており、`MCPConnect` /
設定ファイルの貼り付け / 手動の MCP 起動を一切踏ませない。Rhino 操作の実体（tools /
agents）は公式 [Rhino-MCP-Platform](https://github.com/mcneel/RhinoMCP) に乗り、
CoRhino はその**パネル UX・起動配線・オンボーディング**だけを担う（ツールは持たない）。



https://github.com/user-attachments/assets/824f03e8-8070-4911-a21d-9be97cdc1bc3



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
│        └─ タブごと (= エージェント) に claude / codex を並列起動（＋メニューで選択）
│             ├─ 共有ワークスペース ~/.corhino/workspace/ を cwd に (CLAUDE.md / AGENTS.md)
│             ├─ claude: ~/.corhino/runtime/ のタブ専用 .mcp.json / codex: -c オーバーライド注入
│             ├─ スキル: claude は --plugin-dir、codex は workspace/.agents/skills (symlink) でロード
│             └─ 全タブが同じ http://localhost:<port>/ に接続 (同じ doc を操作)
└── Rhino-MCP-Platform プラグイン (.rhp)
     └── http://localhost:<port>/ で MCP ホストを提供
```

- **direct-HTTP 接続**: claude は `http://localhost:<port>/` の `rhino` MCP サーバに
  直接つながる。node router は使わない（パネルの意味論は「ドックされた“この”Rhino を操作」）。
- **インスタンス単位のポート**: パネルごとに空きポートを採番するので、複数ドキュメントでも
  各パネルが自分の Rhino を操作する。
- **1 ドキュメントに複数エージェント（並列・CLI 混在可）**: 1パネル内のタブを「＋」で増やすと、
  複数のエージェント（claude / codex 混在可）が同じ Rhino を並列に操作する。RhinoMCP ホストはステートレスでツール実行を
  UI スレッドで直列化するため安全。各セッションには専用レイヤ（`s-XXXXXXXX/`）が割り当てられ、
  書き込みが衝突しないようになっている（再開しても同じレイヤに戻る）。
- **セッション隔離**: claude は `--strict-mcp-config --mcp-config <runtime>/.mcp.json` で起動し、
  ユーザーのグローバル/プロジェクト MCP 設定を読み込まない。`~/.claude` を汚さない。codex は
  `--disable plugins` + 既存 MCP サーバの `enabled=false` + `rhino` サーバ追加を `-c` オーバーライドで
  spawn 時にのみ注入する。`~/.codex/config.toml`・auth はいずれも無変更（再ログイン不要）。
- **ユーザー自作スキル**: `~/.corhino/skills/`（初回起動時に骨格を自動生成）を `--plugin-dir` で
  全セッションに読み込む。パネル内の claude に「今の手順をスキルにして」と頼むと、同梱の
  `save-rhino-skill` メタスキルが規約どおり `skills/<name>/SKILL.md` を書き出す（手で置いてもよい。
  書き方は seed される `~/.corhino/skills/README.md` を参照）。全パネル・全タブに載り、
  実行中セッションへの反映は `/plugin reload-plugins`。
- **スキル一覧ドロワー**: タブバー右端の ⚡ で、溜まったスキルの一覧（自作/同梱バッジ・説明）を
  ターミナル横に表示する。保存直後のスキルはライブで現れ、未反映なら ⚠ が付く。「エディタで開く」で
  SKILL.md を直接編集。自作スキルは claude / codex 両方のセッションに載る（同一の `~/.corhino/skills`
  を共有。codex へは workspace の `.agents/skills` symlink 経由）。反映方法は CLI で異なる —
  claude タブはヘッダの ⟳ 一発でアクティブなセッションに反映、codex は実行中セッションへの再読込が
  できないため ⟳ が消え、⚠ は「新しいタブから有効」表示になる。同梱スキル（save-rhino-skill）も
  両 CLI に載る（codex へは workspace の `.codex/skills` symlink 経由）。
- **セッション管理**: 会話は CLI が自動保存しており、タブバー右端の 🕘 で過去セッションを一覧・
  ワンクリック再開できる（タイトルは自動生成、日時つき。開いているセッション以外は
  いつでも再開可能）。タブラベルのダブルクリック（または一覧の ✎）で名前を付けられ、名前は
  再開後も引き継がれる。不要になったセッションは一覧の 🗑（2度押しで確定）で削除できる。全セッションが共有ワークスペース（`~/.corhino/workspace/`）で動くので、
  Rhino を再起動しても履歴が繋がる。claude / codex 両方のセッションが CLI バッジ付きで 1 つの
  タイムラインに並び、クリックすれば該当 CLI で再開される（codex の削除は索引の整合のため
  `codex delete` 経由）。
- **言語**: パネル UI・ダイアログ・生成されるテンプレート（CLAUDE.md 等）は Rhino の言語設定に
  追従する（日本語 Rhino なら日本語、それ以外は英語）。
- **MCP ホスト起動**: `RHINO_MCP_AUTOSTART_PORT` をセットして hidden コマンド `_-MCPSpawn`
  を実行する（`MCPStart` の対話的ポート入力は runscript と相性が悪いため使わない）。

## 必要環境

- **Rhino 8**（macOS: Apple Silicon / Intel、Windows）
- **[Claude Code](https://claude.com/claude-code) CLI** (`claude`) が PATH 上にあること
  - 見つからない場合はパネルがログインシェル (`bash -l` / `cmd.exe`) にフォールバックする
- （任意）**[Codex CLI](https://developers.openai.com/codex/cli)** (`codex`) — あれば ＋メニューから
  codex タブを起動できる。無ければメニューでグレーアウトされるだけで claude 側に影響はない
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

タブバーの「＋」で CLI（Claude Code / Codex）を選び、**同じ Rhino ドキュメントに対して
エージェントをもう1つ**起動できる。タブには CLI バッジ（● claude / ◆ codex）が付く。各タブは
独立したセッション（別履歴）で、セッションごとの専用レイヤ接頭辞（`s-XXXXXXXX/`）が
割り当てられる。各エージェントは自分のレイヤ配下にだけ書き込み、他レイヤは読み取りのみとする
規約で動くので、並列で別々の作業（例: 片方がジオメトリ生成、片方が解析）を進めても衝突しにくい。
タブの「×」でそのエージェントだけ終了できる。

> 注意: レイヤ分離は CLAUDE.md / AGENTS.md による**規約**であり、ホストが強制するわけではない。同じオブジェクトを
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
│   ├── CoRhinoPanel.cs     WebView + xterm.js (タブ式)、ポート採番、複数エージェント起動、MCP ホスト起動
│   ├── SkillsCatalog.cs    スキル一覧ドロワーのデータ層（スキャン + frontmatter パース）
│   ├── SessionsCatalog.cs  セッション一覧ドロワーのデータ層（~/.claude/projects のトランスクリプトスキャン）
│   ├── SessionNames.cs     ユーザーが付けたセッション名の永続化（~/.corhino/session-names.json）
│   ├── SessionLayers.cs    codex セッション ID → レイヤの永続化（~/.corhino/session-layers.json）
│   ├── Workspace.cs        ~/.corhino/workspace/（共有 cwd + CLAUDE.md/AGENTS.md）と runtime/ の .mcp.json、
│   │                       ~/.corhino/skills/（ユーザースキル plugin 骨格）の seed
│   └── RhinoMcpInstaller.cs  Rhino-MCP-Platform の yak オンボーディング
├── Cli/                    CLI アダプタ層（IAgentCli / ClaudeCli / CodexCli — 起動引数・隔離・ID 発見）
├── Pty/                    PTY セッション（Porta.Pty）と CLI 解決
└── Resources/icon.png      パネルアイコン
cc-plugin/                  同梱 Claude Code プラグイン（save-rhino-skill 等のオンボーディングスキル）
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
