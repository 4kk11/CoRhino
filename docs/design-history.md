# CoRhino 設計史 — 意図・判断・検証ログ

このドキュメントの責務は「**なぜ今この形なのか**」の記録だけに絞る——設計の意図、確定/未確定の判断
（D / U）、フェーズ計画、リスク、実機検証の結果、調査したバグ（B）。
- **現在の構造そのものの説明** → [`architecture.md`](architecture.md)
- **未実装・今後の作業** → [`roadmap.md`](roadmap.md)

ここには上記2つを重複させない。

## 意図（なぜやるか）

CoRhino を入れるだけで、Rhino にドックした Claude Code パネルが
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
- **D2: ツールはコピーしない。CoRhino の責務はパネルUX + 起動配線 + オンボーディングのみ。**
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

### Phase 1 — MVP 自動配線（CoRhino 本体）
- 1a: パネル起動時にホスト MCP を自動起動。`CoRhinoPanel` から `RhinoApp.RunScript("_-MCPStart 10500\n", false)`
  を発火（`Rhino-MCP-Platform` 未導入なら skip してログ）。doc 切替時は既存の
  `EndOpenDocument`/`NewDocument` フックで再発火（サーバは doc 単位）。
- 1b: `CoRhinoPanel.StartSession()` の claude 起動引数に Phase 0 で確定した配線を注入
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
当初の仕上げ項目。一部は実装済み（ポート競合フォールバック＝D6、README）。**未了分は
[`roadmap.md`](roadmap.md) に移管**（plugin 配信の更新戦略・接続インジケータ等）。

## リスクと緩和

- **R1（最重要）: cc-plugin の agents が plugin 自前サーバ名に固定束縛**で、http `rhino` に
  繋がらない。→ Phase 0/S3 で判定。ダメなら (a) cc-plugin の `.mcp.json` 1ファイルだけ
  transport 差し替え（tools/agents は触らない最小逸脱）、(b) MVP は agents 無し・素のツール
  直叩きで出し、agents は後追い。
- **R2: ホスト MCP は doc 単位**。新規/切替 doc で再起動が要る → 既存フックで対応。
- **R3: `--mcp-config` がファイルパス必須**かもしれない → 一時 json 生成で回避。
- **R4: yak 導入は Rhino 再起動が必要**でその場で即動かない → オンボーディング文言で吸収。

## 責務境界（最終形）

D2 の帰結としての責務分担（公式に乗る／CoRhino が持つ）の図は、現在の構造として
[`architecture.md`](architecture.md) §1.2 に置く（ここでは重複させない）。要点だけ:
**CoRhino はインターフェース（パネル UX + 起動配線 + ホスト MCP 自動起動 + オンボーディング）だけを持ち、
tools/agents は公式 Rhino-MCP-Platform / cc-plugin に乗る。**

## 実装状況（2026-05-31）

Phase 0 のスパイク（S1/S3）は未実機検証だが、direct-HTTP(D1) 前提で Phase 1a/1b
の製品配線を先行実装した。あわせて、計画外だった **cwd 統一** を新規決定して反映。

### 追加決定 D5: claude の cwd は固定ワークスペースに統一

- **決定**: claude は常に `~/.corhino/workspace` で起動する。アクティブ
  `.3dm` の場所には追従しない（旧 `OnDocChanged` の `cd` 送信は廃止）。
- **理由**: セッション設定（CLAUDE.md / `.mcp.json`）を一元化するため。`.3dm` ごとに
  cwd が散らばると、どのディレクトリの CLAUDE.md が効くか不定になり、非エンジニアに
  とって挙動が読めない。固定 WS なら「常に同じ設定で同じ Rhino を操作」になる。
- **`.mcp.json` の置き場所**: 計画 1b は「プラグインディレクトリ配下に生成」だったが、
  cwd=WS に変更したため **WS 直下に生成**へ更新。config が cwd と同居して自然。

### 実装済み（src/Panel/Workspace.cs, CoRhinoPanel.cs）

- **1a ホスト MCP 自動起動**: `CoRhinoPanel` 生成時に `_-MCPStart 10500` を発火
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

- **B1 MCPStart のコマンドネスト失敗**: パネルが `CoRhino` コマンド経由で構築されると
  コンストラクタ内の同期 `RhinoApp.RunScript("_-MCPStart")` が入れ子になり失敗。
  → `RhinoApp.Idle` ワンショットに遅延させ top-level 実行に修正（CoRhinoPanel.cs）。
- **B2 libporta_pty.dylib のアーキ不一致**: `FlattenNativeLibs` が osx-arm64/osx-x64 を
  同名フラット化し x64 が勝ち、arm64 Rhino で dlopen 失敗→PTY 起動不可だった。
  → `lipo -create` でユニバーサルバイナリ化（csproj）。両 arch で load 可能に。
- **B3 MCPStart で port を runscript 引数に渡せず host 未起動**: `_-MCPStart {port}` の
  `MCPStartCommand` は `GetInteger` で port を対話取得する設計で、runscript からは port を
  消費できず余った数値が "不明なコマンド: 10500" として漏れ、サーバが起動しなかった
  （yak 0.1.3 は doc-open autostart も無いため完全に未起動）。RhinoMCP 側はこの racy な経路を
  嫌って `MCPSpawn`(hidden ScriptRunner) + 環境変数 `RHINO_MCP_AUTOSTART_PORT` を用意している。
  → `EnsureHostMcpStarted` を「env var セット→ `_-MCPSpawn _Enter`」に変更（CoRhinoPanel.cs）。
  env var をセットすると plugin 側 doc-open autostart も抑止されるので port 所有が一意になる。

- **B4 Windows 日本語 IME の入力重複（2026-06-01, 動画報告）**: パネルで日本語を変換すると、最初に確定した語
  （例 `日本語`）が以後の変換のたびに再送されて `日本語日本語日本語…` と増殖し、新しく変換中の語は確定されなかった。
  原因は Windows 限定の helper textarea クランプ `.xterm-helper-textarea { max-width:0; max-height:0 }`（[main.ts:81](../ui/src/main.ts#L81)）。
  これは「合成中に textarea が伸びてパネルが横スクロール → FitAddon → PTY resize → TUI 再描画」のカスケード対策だったが、
  xterm の `CompositionHelper.updateCompositionElements` は IME 用に textarea を `Math.max(bounds, 1)` で 1px 下限に保つ設計で、
  CSS の `max-width:0` がインライン幅を上書きして 0×0 に潰し、xterm が「certain IMEs may break」と警告する条件そのものを踏んでいた。
  0×0 の textarea で Windows/WebView2 の IME が合成位置を追えず、`_compositionPosition` 起点計算がずれて確定済み先頭語を再送した。
  → クランプ値を `0` から `1px` に変更（横スクロール対策は維持しつつ xterm の IME 最小サイズ要件を満たす）。

### Phase 2a 実装（2026-05-31）— yak 自動インストール

未導入時の手動 `yak install` ステップを排除する。`src/Panel/RhinoMcpInstaller.cs` を追加。

- **導入検出**: `PlugIn.PlugInExists(2668d7ed-f507-4a68-8295-8172147a0e39, ...)` で判定。
  GUID は RhinoMCP の `rhino/plugin/Properties/AssemblyInfo.cs` 由来。yak パッケージ dir
  スキャンではなく plugin id で見るのは、ローカル Debug コピー（yak パッケージではない）も
  正しく「導入済み」と判定するため（`yak list` には出ない）。
- **配線（CoRhinoPanel.cs）**: パネル構築時に `IsInstalled()` を判定。導入済みなら従来どおり
  `EnsureHostMcpStarted()`、未導入なら `RhinoMcpInstaller.RunOnboarding()` を起動し MCPStart は
  撃たない。doc 切替の再起動（`OnDocChangedRestartHost`）も `IsInstalled()` ガードを追加。
- **オンボーディング UX**: モーダルがパネル構築を止めないよう `RhinoApp.Idle` ワンショットに遅延。
  確認ダイアログ（Eto `MessageBox` YesNo）を**1回だけ**出し、Yes で yak install を**バックグラウンド
  スレッド**実行。進捗はコマンドライン、完了→「Rhino 再起動」案内／失敗→エラーは Eto ネイティブ
  ダイアログ。再起動が要るのは yak 導入プラグインが起動時しかロードされないため。
- **yak CLI 解決**: macOS は `<Rhino N.app>/Contents/Resources/bin/yak`（実行プロセスの MainModule
  から導出）、Windows は Rhino System dir の `yak.exe`（RhinoCommon.dll と同居）。
- **検証**: `yak search Rhino-MCP-Platform` で既定サーバに `0.1.3`（prerelease ゲートなし）を確認済み。
  Debug ビルド成功。実機の未導入→install→再起動フローは要実機確認。

### 追加決定 D6: ホスト MCP ポートと cwd を per-instance 化（D5 の固定ポート/単一 WS を改訂）

- **背景の不具合**: Rhino を2つ並べる（Mac では実プロセスは1つで、新規ドキュメント＝
  別ウィンドウ＋別パネルになる）と、全パネルが固定ポート `10500` で `MCPStart` し、全
  `.mcp.json` も `localhost:10500` を指すため、どのパネルの claude も「10500 を握った
  ドキュメント」に繋がっていた（B のパネルの指示が A に反映される）。
- **決定**: パネルごとに**空きポートを採番**（`Workspace.ResolveFreePort()`、10500 から
  bind 試行で最初の空き）し、その port で `MCPStart` し、その port を指す `.mcp.json` を
  **per-instance の cwd**（`~/.corhino/sessions/<port>/`）に生成する。これで
  各パネルの claude は自分のドキュメントのホストに繋がる。
- **D5 からの改訂点**:
  - 固定 `HostPort=10500` → 動的採番（`CoRhinoPanel._hostPort`）。`MCPStart`/`.mcp.json`/
    doc 切替時の再起動すべて同じ port を使う。
  - 単一 `workspace/` → `sessions/<port>/` の per-instance dir。CLAUDE.md は共有テンプレ
    から各 dir に seed（初回のみ）、`.mcp.json` は port から導出されるため常に上書き。
  - cwd を分けたことで claude のセッション履歴（`~/.claude/projects/<cwd>`）も per-instance
    に分離され、並行セッションが干渉しない。
- **検証（2026-05-31, ユーザー実機 pass）**: 2 ドキュメント／2 パネルで、各 claude が
  他方がアクティブでも自分のドキュメントに正しく反映されることを確認（正しく分離）。
  - 注意: `run_python` の `scriptcontext.doc` はアクティブ doc を指すため、ホスト直叩きの
    素朴な count 比較ではアクティブ doc を拾う。実ルーティングはホストのバインド doc 経由で
    正しく分離される。

## セッションのライフサイクルと再起動（設計判断）

### 課題
`/exit` で Claude Code を終了するとパネルが無言で固まり、パネルを閉じて `CoRhino`
コマンドで開き直しても再起動しなかった。原因は2点の相互作用:

1. `PtySession` が子プロセス終了後も `_connection` を保持し続け、`IsRunning` が
   `true` のままだった（終了を検知しても内部状態をクリアしていなかった）。
2. Rhino の `PanelType.System` パネルはインスタンスがキャッシュされ、閉じても
   `Dispose` されず、再オープン時にコンストラクタ（＝1回限りの `StartSession`）が
   再実行されない。死んだ PTY を抱えたまま同じインスタンスが再表示されていた。

### 採用した方針
- **終了検知の一元化**: `PtySession.HandleExit` を追加し、`ProcessExited` と ReadLoop
  の EOF の両経路から呼ぶ（`Interlocked` で1回だけ実行）。接続を dispose して
  `IsRunning` を `false` にし、`Exited` を発火する。ホストによって `ProcessExited`
  が発火しないケースに備え EOF も終了シグナルとして扱う。
- **再起動トリガはコマンド再実行に置く**: `OpenCoRhinoPanelCommand` が `OpenPanel`
  後に `Panels.GetPanel<CoRhinoPanel>(doc)` で生存インスタンスを取得し
  `EnsureSessionAlive()` を呼ぶ。×ボタンでの再オープンではなく `CoRhino` コマンドを
  再起動の入口とする（System パネルは Dispose されない前提に依存しないため確実）。
- **PtySession は使い捨て**: `StartAsync` 2回目例外の仕様は維持し、再起動時は
  古い session を `Dispose` して新インスタンスを生成・再配線する。状態リセットが
  確実になる。MCP ホスト・ポート・セッションディレクトリ（`_hostPort`）はパネル
  存続中そのまま再利用するため再 Spawn は不要。
- **パネル自動クローズ**: 終了時は `Panels.ClosePanel(GUID, RhinoDoc)` でこのパネルを
  閉じる（`RhinoDoc.FromRuntimeSerialNumber(_documentSerialNumber)` で自分の doc を
  解決し、別 doc のパネルを誤って閉じない）。端末に終了表示を残すのではなく画面から
  消す。キャッシュされたインスタンスは生存し MCP ホスト・ポート・セッション
  ディレクトリを保持するので、再度 `CoRhino` で開けば `EnsureSessionAlive` が新
  セッションを起動する。再起動時のみ `onSessionRestart()`（`term.reset()`）を webview
  に送り、新セッションをクリーンな画面に描画する。

### 再入防止
reopen 時の再 spawn 経路は `CoRhinoPanel._respawning` を `Interlocked` で管理し、`EnsureSessionAlive`
の二重実行を防ぐ。`createTab` は毎回新しい agentId を採番するので二重 spawn の懸念がなくガード不要
（D7 以前の単一セッション期は `_starting` フラグだったが、タブ多重化で役割を `_respawning` に置換）。

## 追加決定 D7: 1 ドキュメントに複数 claude（タブ並列エージェント, 2026-06-03）

### 意図（なぜやるか）
同一 Rhino ドキュメントに対し、複数の Claude Code を**並列で独立作業**させたい（例: 1 エージェントが
ジオメトリ生成、別エージェントが解析/レビュー）。

### 調査で判明した決定的事実（公式 Rhino-MCP-Platform 側）
`.06_Projects/RhinoMCP` を読んで確認:
- HTTP ホストは**ステートレス**。`initialize` ハンドシェイク不要で、各ツール呼び出しが自己完結した
  JSON-RPC POST（`rhino/plugin/Server/McpEndpoint.cs`）。→ 1 ポートに複数クライアントが同時接続できる。
- ホストは `RuntimeSerialNumber` で **doc 単位**にキー付き、`RhinoDoc` を DI シングルトンで固定
  （`RhMcpHost.cs`, `McpServer.cs`）。→ そのポートに繋いだ全 claude は同じ doc を操作する。
- ツール実行は `RhinoApp.InvokeOnUiThread` で **UI スレッドに直列化**（`ToolRegistry.cs`）。
  → 複数 claude の同時 POST でもクラッシュしない。

**帰結**: 「1 doc に複数 claude」は**ホスト無改修**で成立する。CoRhino 側を「1パネル内に複数タブ＝
複数 PTY＝複数 claude、全タブが同一ホストポート共有」に拡張するだけでよい。

### 決定
- **案A: 1 パネル内タブ式**を採用。ポートが自然に doc 単位に保たれ（既に 1 パネル = 1 doc）、`_hostPort`
  据え置きで済む。Rhino のパネル機構（`GetPanel` の per-doc キャッシュ）と非衝突。ホスト起動は doc 初回 1 回のまま。
- **却下した案**:
  - 案B 複数パネル: `GetPanel` の per-doc 単一キャッシュとドック UX に反する。同一 doc に複数ホストを
    立てると RhinoMCP の `RestartOnPort` で互いを止め合う。
  - 案C オーケストレータ（claude のサブエージェントで多重化）: 将来併用可だが、「独立並列」主目的に対し
    UI 体験が弱い。
- **論理衝突はレイヤ・ソフト分離で回避**: ホストはクライアント間調整をしない（per-client 状態なし）。
  UI スレッド直列化は同時実行クラッシュは防ぐが論理衝突（A が消した object を B が参照 等）は防がない。
  各エージェントの CLAUDE.md に担当レイヤ接頭辞（`agent-N/`）を埋め、書き込みは自レイヤのみ・他レイヤは
  読み取りのみと規定する。ホスト改修ゼロで、レイヤ＝帰属の可視化も兼ねる。**アドバイザリ**（遵守頼み）で
  あることは許容し、強制力（ホスト側リース）は衝突が痛くなってからの将来作業（roadmap）。
- **将来フックとして `X-CoRhino-Agent` ヘッダを今入れる**: 各タブの `.mcp.json` に一意のエージェント ID を
  ヘッダで付ける。今は誰も読まないが、後でホスト側に帰属ログ/リースを足すとき再設計不要にする。
  設計境界に沿う（識別子の注入は CoRhino、調整ツール本体は公式 Rhino-MCP-Platform）。

### 実装（src/Panel/CoRhinoPanel.cs, Workspace.cs, ui/src/main.ts）
- パネルは単一 `_session` → `Dictionary<string, AgentTab> _tabs`。agentId は `agent-N` 連番。
- WebView プロトコルを **agentId 多重化**（`createTab`/`closeTab`/`stdin`/`resize` ↔ `onTabCreated`/
  `onPtyData`/`onSessionRestart`/`onTabClosed`/`onTabsReset`）。
- session dir を per-agent（`sessions/<port>/<agentId>/`）化。`.mcp.json` に `X-CoRhino-Agent` ヘッダ、
  CLAUDE.md に担当レイヤ節を seed。
- UI はタブバー（汎用「＋」で任意個・×で閉じる・クリック切替）＋ タブごとの xterm。
- 最後のタブが exit したらパネルを閉じ、reopen で agent-1 を再 spawn。
- **検証**: ビルド pass（`dotnet build src/CoRhino.csproj`、tsc strict + vite, 0 警告）。
  実機の「2 タブ → 別レイヤにジオメトリ生成 → 両方反映・分離」「片タブ /exit で他タブ生存」は要実機確認。

### D7 レビュー反映（Codex セカンドオピニオン, 2026-06-03）

初版を Codex にレビューさせ、見つかった並行性/ライフサイクルのバグを修正した。

- **B5 ×ボタンでタブが消えない（High）**: `CloseTab` が `PtySession.Dispose()` だけを呼んでいたが、`Dispose` は
  `_exitedRaised` を立て `Exited` を抑止するため、`OnSessionExited` 経由の後片付け（`_tabs` 除去・`onTabClosed`・
  Terminal 破棄・空ならパネルクローズ）が一切走らなかった。→ 撤去経路を **`RemoveTab(tab)` に一本化**し、
  exit / ×ボタン / 起動失敗すべてここを通す。`CloseTab` は `RemoveTab` を直接呼ぶ。
- **B6 id 再利用で遅延コールバックが別タブを誤操作（High）**: コールバックが agentId 文字列だけを束縛しており、
  reopen で `_nextAgentNum` を 1 に戻すと、古い agent-1 の遅延 `DataReceived`/`Exited` が新 agent-1 に当たり得た。
  → コールバックは **`AgentTab` インスタンス**を束縛し、`RemoveTab`/`OnPtyData` で `ReferenceEquals` 確認して stale を破棄。
- **B7 起動中タブの二重 spawn（Critical）**: `EnsureSessionAlive` が `IsRunning` だけ見ていたが、`IsRunning` は
  `SpawnAsync` 戻りまで false。構築直後にコマンドが `EnsureSessionAlive` を呼ぶと起動中の agent-1 を消して
  二重起動し得た。→ `AgentTab.Starting`（volatile）を追加し、起動中も「生存」とみなす。
- **B8 spawn 中 Dispose で PTY リーク（High）**: `PtySession.Dispose` が pending spawn を考慮せず、`SpawnAsync` 戻り後に
  `_connection` が代入されると生きたプロセスが残り得た。→ `_disposed`（volatile）を追加。`StartAsync` は `SpawnAsync`
  戻り後に再チェックし、破棄済みなら自分で Kill/Dispose。
- **起動失敗時の後始末（Medium）**: `StartSession` の catch で `RemoveTab` を UI スレッドへ marshal し、死んだタブを残さない。
- **UI: `term.open` 失敗のロールバック（Medium）**: 失敗時に `term.dispose()`・DOM 除去・`tabs.delete` で半端な状態を残さない。
- **メッセージ堅牢化（Low）**: `type` を `TryGetProperty` で取得し、ハンドラの catch を `Exception` に広げて
  version-skew な1メッセージでポンプを壊さない。

修正後ビルド pass（0 警告）。B5 は実機の `/exit` 経路だけ試して×経路を見落としていた退行で、レビューで捕捉した。

### 構造そのものの説明 → [`architecture.md`](architecture.md) §5.2 / 残作業 → [`roadmap.md`](roadmap.md)（ここでは重複させない）。

## 追加決定 D8: ユーザー自作スキルの動的配線（2026-07-07）

**意図**: ユーザーが各々、会話の中から自分のスキル（モデリング手順・ノウハウ）を動的に作成・蓄積し、
以後の全セッションで使えるようにしたい。スキル内容の統括管理は Claude Code の plugin 機構に乗せる。

### 判断と選択理由

- **`~/.corhino/skills/` を local plugin として `--plugin-dir` で全セッションに注入**:
  plugin 由来のスキルは cwd 非依存なので、per-agent session dir で起動する CoRhino の構成でも
  確実に載る。`--plugin-dir` は repeatable なので、将来の配布用プラグイン（Platform 側）は
  2 本目を足すだけで共存できる。
- **`~/.claude/skills`（user scope）は使わない**: 技術的には隔離起動でも読まれるが、ユーザーの
  グローバル環境に Rhino スキルが混ざり「`~/.claude` を汚さない」（D3）と矛盾する。専用 dir に分離。
- **CoRhino が持つのは骨格 seed と配線のみ**: `.claude-plugin/plugin.json` + `skills/` + README を
  初回に seed（以後ユーザー所有・上書きしない＝session CLAUDE.md と同じ方針）。スキル本文は
  リポジトリに持たない。Rhino ノウハウを CoRhino に抱え込まない D2 の境界は維持される。
  骨格生成のタイミングは StartSession 時（spawn 直前）。存在保証が spawn と同じ経路に乗り、
  別途ライフサイクルを増やさない。
- **反映モデル**: スキルは起動時ロードなので、新規タブ/セッションには自動で載り、実行中セッションは
  `/plugin reload-plugins` で再読み込みする。この案内は seed する README に含めた。

### 実装（src/Panel/Workspace.cs, CoRhinoPanel.cs）

- `Workspace.UserSkillsRoot` / `EnsureUserSkillsPlugin()` を追加。
- `StartSession` の claudeArgs に `--plugin-dir Workspace.EnsureUserSkillsPlugin()` を追加。
- **検証**: ビルド pass（`dotnet build src/CoRhino.csproj`、0 警告）。実機の
  「skills/ に SKILL.md を置く → パネル内 claude が発動する」は要実機確認。

### 今後（配布用プラグイン・`save-rhino-skill` メタスキル）→ [`roadmap.md`](roadmap.md)（ここでは重複させない）。

### D8 追記: `save-rhino-skill` メタスキルの同梱（2026-07-07）

当初「メタスキルは Platform 側の配布用プラグインに置く」と整理して roadmap 送りにしたが、再検討して
**CoRhino 同梱**に変更した。

- **境界の再解釈**: `save-rhino-skill` が教えるのは Rhino 操作ノウハウではなく、**CoRhino 自身の規約**
  （`~/.corhino/skills` の配置・SKILL.md の書式・`/plugin reload-plugins` での反映）。規約の所有者は
  CoRhino なので、これはオンボーディングコンテンツであり D2 に抵触しない（→ architecture.md 1.2 の例外注記）。
- **ユーザー plugin への seed ではなく同梱 plugin にした理由**: seed 方式（初回コピー後ユーザー所有）だと
  メタスキルの更新が既存ユーザーに届かず、CoRhino 所有のコンテンツがユーザー空間に混ざる。同梱 cc-plugin
  なら .rhp の更新と一緒に配布され、所有権も分離されたまま（ユーザー plugin は純粋にユーザーの物）。
- **配送**: リポジトリ `cc-plugin/` → ビルドで `$(TargetDir)cc-plugin/`（web/ と同じ経路）→ spawn 時に
  2 本目の `--plugin-dir`。ディレクトリが無い install では黙ってスキップ（スキル無しでもセッションは成立）。
- **検証**: ビルド pass（0 警告）、`claude plugin validate cc-plugin` pass、Rhino plugin dir への配送を確認。
  パネル内 claude での発動は要実機確認。
