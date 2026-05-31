# RhinoClaudePanel × 公式RhinoMCP 統合計画

## 意図（なぜやるか）

RhinoClaudePanel を入れるだけで、Rhino にドックした Claude Code パネルが
**今いるその Rhino を操作できる**状態にする。非エンジニアが対象なので、
手動の `MCPConnect` / `/plugin install` / 設定貼り付けを一切踏ませない。

Rhino 操作の中身（tools / agents / skills）は**公式 RhinoMCP に乗る**。
McNeel が育てるので、こちらはツールを持たず**インターフェース（パネル + 配線 +
オンボーディング）だけ**を作る。フォーク（vendoring）はしない。

## 確定した設計判断と選択理由

- **D1: 接続は direct-HTTP（`http://localhost:10500`）。node connector / router は使わない。**
  - 理由: パネルの意味論は「ドックされた“この”Rhino を操作」。router は Rhino を
    spawn して別インスタンスを管理する設計で、この用途と噛み合わない。direct-HTTP なら
    Node.js 依存も router も消える。router のウリ（即接続/Rhino起動/多重/クラッシュ回復/
    8・9同時）はパネル内蔵トポロジーでは全て無効。
- **D2: ツールはコピーしない。RhinoClaudePanel の責務はパネルUX + 起動配線 + オンボーディングのみ。**
  - 理由: 「公式が勝手に育つ」に乗るため。vendoring は方針と逆（自分でフォーク保守が発生）。
- **D3: 設定はセッション単位で注入（起動フラグ / `--settings`）。グローバル `~/.claude` を汚さない。**
  - 理由: Rhino から開いた claude のときだけ rhino ツールが付くのが自然。他プロジェクトに漏らさない。
- **D4: ホスト側 MCP サーバはパネルが自動起動（`MCPStart` 相当）。ユーザーにコマンドを打たせない。**

## 未確定 → Phase 0 で実機検証する

- **U1: cc-plugin の agents/commands は、こちらが `--mcp-config` で定義した http `rhino`
  サーバのツールに束縛されるか？** plugin 提供サーバは `mcp__plugin_rhino-mcp_rhino__*`、
  こちら定義は `mcp__rhino__*` で名前空間が異なる可能性。ここが統合の最大リスク。
- **U2: `--mcp-config` はファイルパスのみか、インライン JSON 文字列も可か。**
- **U3: plugin 提供 `rhino` と `--mcp-config` の `rhino` が衝突したときどちらが勝つか。**
- **U4: `--plugin-dir` でローカル cc-plugin をロードしたとき、その `.mcp.json`(node) を
  こちらの http 定義で上書き/無効化できるか（`--strict-mcp-config` 併用含む）。**

## フェーズ計画

### Phase 0 — スパイク（製品コードを書かず不確定を潰す）
手動で Rhino に `Rhino-MCP-Platform`(yak) を入れ、`MCPStart` で 10500 を起動した状態で、
ターミナルから claude を各フラグで起動して挙動を観察する。

- S1: `claude --mcp-config rhino-http.json`（`{"mcpServers":{"rhino":{"type":"http","url":"http://localhost:10500"}}}`）
  で「今のドキュメントに何がある?」が通るか。→ direct-HTTP 経路の成立確認。
- S2: そのときツール名が `mcp__rhino__*` で見えるか（`/mcp` で確認）。
- S3: `--plugin-dir references/RhinoMCP/cc-plugin` で agents/commands をロードしつつ、
  rhino サーバを http 定義に差し替えて `/rhino-mcp:scene` や rhino-modeller が動くか。
  → U1/U3/U4 の判定。
- **Phase 0 の成果物**: D1 配線の確定版コマンドライン + plugin 配信方式の最終決定
  （marketplace via `--settings` か、`--plugin-dir` + submodule か）。

### Phase 1 — MVP 自動配線（RhinoClaudePanel 本体）
- 1a: パネル起動時にホスト MCP を自動起動。`ClaudePanel` から `RhinoApp.RunScript("_-MCPStart 10500\n", false)`
  を発火（`Rhino-MCP-Platform` 未導入なら skip してログ）。doc 切替時は既存の
  `EndOpenDocument`/`NewDocument` フックで再発火（サーバは doc 単位）。
- 1b: `ClaudePanel.StartSession()` の claude 起動引数に Phase 0 で確定した配線を注入
  （`--mcp-config <生成した rhino-http.json>` ± plugin 有効化フラグ）。json は
  プラグインディレクトリ配下に生成。
- 1c: E2E 確認: 箱を1つ置いた Rhino でパネルを開き、「何がある?」→ 箱を答える、
  「球を足して」→ 実際に追加される、まで通す。

### Phase 2 — 非エンジニア向けオンボーディング
- 2a: `Rhino-MCP-Platform` 導入検出（ロード済みプラグイン or yak 配置）。未導入なら
  yak CLI を解決して `yak install Rhino-MCP-Platform` を実行 → 再起動を案内（公式
  launcher のチャット自己ブートストラップは direct-HTTP では使わないので自前で握る）。
- 2b: claude 本体の導入確認（既存 `CliResolver`）。未導入時の導線整理。
- 2c: 失敗時のパネル内ステータス表示（「Rhino-MCP 未接続」等）。

### Phase 3 — 仕上げ
- ポート競合時のフォールバック（10500 使用中なら採番）。
- plugin 配信の更新戦略（marketplace 自動 or submodule バンプ）。
- パネル UI に接続インジケータ。
- `README` / 利用手順。

## リスクと緩和

- **R1（最重要）: cc-plugin の agents が plugin 自前サーバ名に固定束縛**で、http `rhino` に
  繋がらない。→ Phase 0/S3 で判定。ダメなら (a) cc-plugin の `.mcp.json` 1ファイルだけ
  transport 差し替え（tools/agents は触らない最小逸脱）、(b) MVP は agents 無し・素のツール
  直叩きで出し、agents は後追い。
- **R2: ホスト MCP は doc 単位**。新規/切替 doc で再起動が要る → 既存フックで対応。
- **R3: `--mcp-config` がファイルパス必須**かもしれない → 一時 json 生成で回避。
- **R4: yak 導入は Rhino 再起動が必要**でその場で即動かない → オンボーディング文言で吸収。

## 責務境界（最終形）

```
公式が育てる（触らない）:
  Rhino-MCP-Platform (yak)  … tools・HTTPサーバ(10500)
  cc-plugin                 … agents / commands / skills

RhinoClaudePanel が持つ:
  パネルUX + ホストMCP自動起動 + claude起動配線(direct-HTTP) + オンボーディング
```

## 実装状況（2026-05-31）

Phase 0 のスパイク（S1/S3）は未実機検証だが、direct-HTTP(D1) 前提で Phase 1a/1b
の製品配線を先行実装した。あわせて、計画外だった **cwd 統一** を新規決定して反映。

### 追加決定 D5: claude の cwd は固定ワークスペースに統一

- **決定**: claude は常に `~/.rhino-claude-panel/workspace` で起動する。アクティブ
  `.3dm` の場所には追従しない（旧 `OnDocChanged` の `cd` 送信は廃止）。
- **理由**: セッション設定（CLAUDE.md / `.mcp.json`）を一元化するため。`.3dm` ごとに
  cwd が散らばると、どのディレクトリの CLAUDE.md が効くか不定になり、非エンジニアに
  とって挙動が読めない。固定 WS なら「常に同じ設定で同じ Rhino を操作」になる。
- **`.mcp.json` の置き場所**: 計画 1b は「プラグインディレクトリ配下に生成」だったが、
  cwd=WS に変更したため **WS 直下に生成**へ更新。config が cwd と同居して自然。

### 実装済み（src/Panel/Workspace.cs, ClaudePanel.cs）

- **1a ホスト MCP 自動起動**: `ClaudePanel` 生成時に `_-MCPStart 10500` を発火
  （`RhinoApp.RunScript`）。`MCPStartCommand` は `StartOrRestart` なので再発火は安全。
  サーバは doc 単位のため `EndOpenDocument`/`NewDocument` で再発火。未導入時は
  RunScript=false をログして継続（install 導線は Phase 2）。
- **1b claude 起動配線**: `StartSession()` で WS を `EnsureInitialized()`（初回のみ
  CLAUDE.md と direct-HTTP の `.mcp.json` を生成、既存は上書きしない）し、
  `claude --strict-mcp-config --mcp-config <WS>/.mcp.json` を WS で起動。
  `--strict-mcp-config` でユーザーのグローバル/プロジェクト MCP を排除し、`rhino`
  （http://localhost:10500/）だけをロードする。

### Phase 0 / Phase 1 実機検証結果（2026-05-31, 実機 pass）

ローカル Debug ビルドの Rhino-MCP-Platform をロードした Rhino 8（Apple Silicon）で検証。

- **S1 direct-HTTP（U1/U2）= pass**: 生成済み WS の `.mcp.json` で
  `claude -p --strict-mcp-config --mcp-config .mcp.json` を headless 実行。
  - READ: 「current Rhino doc のオブジェクト列挙」→ 箱（Extrusion）1個を正しく報告。
  - WRITE: 「半径5の球を追加」→ 追加成功。rhino-cli 側から `["Brep","Extrusion"]` を独立確認。
  - ツール名前空間 = **`mcp__rhino__*`**（plugin 版 `mcp__plugin_...` ではない）。U1 OK。
  - `--strict-mcp-config` でユーザーのグローバル MCP を排除できることも確認。
- **1a ホスト MCP 自動起動 = pass**: パネル構築で `MCPStart 10500` 発火→
  `curl localhost:10500 tools/list` が rhino tools を返す（"host MCP up"）。
- **1b パネル内 claude 起動 = pass**: パネルを開くと cwd=WS で claude が spawn（pid 取得）、
  `execute-panel-js` で xterm バッファを読むと claude Code TUI が WS で稼働中と確認。

### 検証中に直したバグ

- **B1 MCPStart のコマンドネスト失敗**: パネルが `RhinoClaude` コマンド経由で構築されると
  コンストラクタ内の同期 `RhinoApp.RunScript("_-MCPStart")` が入れ子になり失敗。
  → `RhinoApp.Idle` ワンショットに遅延させ top-level 実行に修正（ClaudePanel.cs）。
- **B2 libporta_pty.dylib のアーキ不一致**: `FlattenNativeLibs` が osx-arm64/osx-x64 を
  同名フラット化し x64 が勝ち、arm64 Rhino で dlopen 失敗→PTY 起動不可だった。
  → `lipo -create` でユニバーサルバイナリ化（csproj）。両 arch で load 可能に。

### 残（後続フェーズ）

- S3（cc-plugin の agents/commands を `--plugin-dir` で載せ http rhino に束縛できるか）は
  MVP では未着手。agents 無しの素のツール直叩きで E2E は成立済み。
- ポート競合フォールバック（Phase 3）、未導入時の yak 自動 install（Phase 2）。
