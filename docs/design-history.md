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

## 追加決定 D9: Skills Drawer（スキル一覧ドロワー）（2026-07-07）

**意図**: D8 でスキルが会話から動的に溜まるようになった一方、「何が溜まっていて・何がロード済みか」を
確認する手段が会話しかなく、特に**保存したのに発動しない（reload 忘れ）**が非エンジニアには原因不明の
挙動になる。パネル UX は CoRhino の責務そのものなので、可視化と reload のワンクリック化をパネルで提供する。
設計書: [`plans/skills-drawer-design.md`](plans/skills-drawer-design.md)。

### 判断と選択理由

- **右サイドドロワー（260px 固定・アニメーションなし）**: ターミナルと同時に見えるので
  「一覧を見ながら claude に依頼」できる。オーバーレイや専用タブは会話と排他になる。
  アニメーションを付けないのは transition 中の FitAddon 連打を避けるため。
- **needsReload = SKILL.md の mtime × タブごとの `SkillsLoadedAtUtc`**: claude 内部のロード状態は
  外から読めない。spawn 時に初期化・⟳ で now に更新するタイムスタンプとの mtime 比較は近似だが、
  誤検知は「⟳ を押せば必ず正しい状態に収束する」方向（false ⚠）にしか倒れない。タブごとに
  spawn 時刻が違うため、ドロワーは常に**アクティブタブ視点**で表示する。
- **⟳ は stdin 直叩きではなく専用メッセージ `reloadSkills`**: UI が PTY に直接書くと C# が reload の
  事実を知れず ⚠ が消えない。「PTY 注入 + 時刻更新 + 一覧再送」を C# 側で 1 メッセージに束ねる。
- **watcher push は常時・全タブ分、UI 側でフィルタ**: C# はアクティブタブを知らない（知るには開閉・
  タブ切替の状態同期が要る）。全タブ分 push して UI が「アクティブタブの payload だけ描画・閉時は無視」
  する方が状態機械が不要で壊れにくい。タブ数は高々数個なので冗長スキャンは実害なし。
- **`openSkill` のパス検証**: WebView からのメッセージで任意パスを `Process.Start` させないよう、
  直近 Scan が返した path 集合（`IsKnownSkillPath`）に含まれるものだけ開く。
- **YAML ライブラリを入れない**: 読むのは frontmatter の `name:`/`description:` 単一行値のみ。
  依存追加と .rhp サイズ増に見合わない。パーサは純粋関数に保ち将来のテスト導入に備えた。

### 実装（SkillsCatalog.cs 新規、CoRhinoPanel.cs、ui/src/skills-drawer.ts 新規、main.ts）

- メッセージ 3 種（`listSkills` / `openSkill` / `reloadSkills`）と `onSkillsList` を追加（→ architecture.md §3.2）。
- `FileSystemWatcher`（`~/.corhino/skills/skills/` 再帰監視）+ 300ms one-shot Timer デバウンス。
  watcher イベントはタイマー再アームのみ、push は UI スレッドに marshal。Dispose 直後の在飛行イベントが
  破棄済み Timer に触って ObjectDisposedException をスレッドプールへ漏らさないよう catch を入れた。
- **検証**: `dotnet build` pass（0 警告）・`tsc --noEmit` 0 エラー。実機（ドロワー開閉のリサイズ・
  ライブ更新・⚠→⟳→● 遷移・openSkill）は未確認 → roadmap。Codex レビューゲートは codex CLI の
  認証切れで未消化（代替に汎用レビューエージェントで1パス実施）→ roadmap。
- **代替レビューの指摘と対応**: (1) watcher 起点のスキャンがディレクトリ削除と競合すると UI スレッドに
  例外が漏れる → 列挙を try/catch（削除はまさに watcher を発火させる操作）。(2) frontmatter の
  block scalar（`description: >` 等）が literal `">"` になる → フォールバック扱いに。(3) 閉じフェンスの
  無い `---` は本文全体を frontmatter 扱いしてしまう → 閉じフェンス必須に。(4) ⚡ ボタンがタブ増加で
  スクロールアウト（`margin-left:auto` は overflow で無効）→ タブ列を `#tabscroll` に分離し ⚡ を固定。
  (5) watcher の `Error` 未購読でサイレント死 → ログ追加（V1 は再生成しない）。claude ビジー時の
  ⟳ による false ● とタブ死亡時の残留表示は既知の限界として設計書に明記（V1 許容）。

## 追加決定 D10: セッション管理（安定ディレクトリ + Sessions Drawer + タブ命名）（2026-07-07）

**意図**: タブを閉じる・Rhino を落とすと会話に戻る手段がなかった。実際には claude 自身が
`~/.claude/projects/<cwd-slug>/<sessionId>.jsonl` に全履歴を残しており `--resume <id>` で再開できるが、
resume は **cwd 依存**で、当時の cwd `sessions/<port>/<agentId>` はポート採番に依存していた——
実際に 10500 が塞がれて 10501 に採番された時点で履歴が分断された（両 dir が実在した）。
そこで (1) cwd をポート非依存に安定化し、(2) 過去セッションをドロワーで一覧・ワンクリック再開、
(3) セッション（=タブ）への命名を導入した。プラン: `~/.claude/plans/`（親リポジトリ側で承認済み）。

### 判断と選択理由

- **`sessions/<agentId>/` にポート層を削除（D6 の cwd 部分を改訂）**: D6 の目的は「どの Rhino を操作するか」の
  分離で、それはポートと `.mcp.json` の中身が担う。cwd までポートに縛る必然はなく、履歴の連続性を壊す
  だけだった。`.mcp.json` は spawn 毎に現在ポートで書き直す（claude は起動時に一度読むだけなので安全）。
  旧 `sessions/<port>/` の履歴は**移行しない**（pre-alpha 方針。dir を手で mv しても slug が変わるので
  resume は復活しない——履歴は諦めるのが正しい）。
- **スロット割当を単調連番 → 最小空き番号に**: 履歴は cwd キーなので、agent-27 まで増える連番では
  ディレクトリが散らばる。少数の安定スロットに蓄積させる。id 再利用の安全性は既存の `ReferenceEquals`
  ガード（もともと reopen の id 再生のために存在）がそのまま担保する。
- **spawn 時に `--session-id <GUID>` で ID を固定**: claude が採番する ID を後から知る手段はログの
  パースしかない。C# が先に採番して渡せば、タブ⇄トランスクリプトの対応が常に既知になり、
  リネームの永続キー・実行中判定・resume の重複防止すべてがこの上に載る。
  既知の限界: `/clear` は ID を黙って回転させる（V1 は文書化のみ）。
- **resume はホームスロット限定・占有中は不可**: `--resume` は同じ cwd からしか効かない。占有スロットへの
  同居 spawn は同一レイヤ接頭辞の並列 claude を生み、D7 のレイヤ規約と履歴の分離を両方壊すので許可しない
  （UI はグレー表示、C# 側も defense-in-depth で拒否）。`--fork-session` での別スロット展開も cwd 制約で
  不可能なので採らない。
- **allowlist + 存在検証**: `resumeSession` は直近 Scan が返した sessionId のみ受け付け（openSkill の
  `IsKnownSkillPath` と同型）、spawn 前に jsonl の存在を再検証する。消えた ID で claude が即死すると
  `RemoveTab` → 唯一のタブならパネルごと閉じる、まで連鎖するため。
- **空セッションのフィルタ**: claude は spawn しただけで jsonl（mode / file-history-snapshot 行）を作る。
  非メタ user 行も ai-title も無いファイルは一覧に出さない。これが無いと開いて閉じただけのタブが
  全部ゴミ行になる。
- **タイトル抽出はチャンク読み（head 64KB + tail 256KB）**: トランスクリプトは MB 級で全読みは
  スキャン毎に高くつく。新フォーマットに `"type":"summary"` は無く、タイトルは `ai-title` 行の
  **最後の出現**（fallback: `last-prompt` → 最初の非メタ user プロンプト）。チャンク境界は最初の改行まで
  捨てて UTF-8 破断を回避。スキャンは BG スレッド + `Interlocked` single-flight。
- **命名は `~/.corhino/session-names.json`（sessionId キー）を C# が所有**: claude の `--name` は
  ephemeral（PID キーのランタイムレジストリ）で永続しない。sessionId キーなら /exit → resume を跨いで
  名前が復元される。タブラベルは 手動名 > ai-title > agentId の優先で、`onTabCreated` に初期ラベルを
  同梱（後追い push だと agent-N が一瞬見える）。
- **`onTabCreated(agentId)` → `onTabCreated({agentId, sessionId, label})` の破壊的変更**: pre-alpha
  方針どおりシム無しで両側同時変更。UI がタブから `renameSession` を送るのに sessionId が要る。

### 実装（Workspace.cs、CoRhinoPanel.cs、SessionsCatalog.cs 新規、SessionNames.cs 新規、ui/src/sessions-drawer.ts 新規、main.ts、skills-drawer.ts）

- メッセージ 3 種（`listSessions` / `resumeSession` / `renameSession`）と `onSessionsList` / `onTabLabel`、
  `onTabCreated` のペイロード化（→ architecture.md §3.2）。
- ドロワーは skills と排他（`close()` API を両ドロワーに追加、開いた側がもう片方を閉じる）。
- タブラベルのダブルクリック → インライン input（Enter/Esc/blur）。反映は C# の `onTabLabel` push 待ち
  （楽観描画しない＝保存された値だけが表示される）。
- **検証**: `dotnet build` pass（0 警告）・`tsc --noEmit` 0 エラー。実機確認は未 → roadmap。
  Windows の slug エンコーディング（`C:\` の `\`・`:`）は未検証 → Windows で完了宣言する前の必須ゲート。

### レビューの指摘と対応（同日）

汎用レビューエージェント（+ Codex）のアドバーサリアルレビューで修正した実指摘:

- **(H1) スロット占有がパネルローカルだった**: スロット dir がポート非依存＝プロセスグローバルになったのに、
  `AllocateSlot` が自パネルの `_tabs` しか見ていなかった。macOS でも doc ごとに別パネルが**同一プロセス内**に
  共存するため、2パネルが agent-1 を取り合い、片方の `EnsureSession` が他方の `.mcp.json` を別ポートで
  書き換える（＝**別の Rhino doc を操作する claude**）恐れがあった。プロセス全体の static `ClaimedSlots`
  （UI スレッド専有、`RegisterTab`/`UnregisterTab` に一元化）で占有を仲裁。resume の busy 判定・ドロワーの
  スロット使用中バッジも同セットを見る。クロス**プロセス**（Windows の2 Rhino）は引き続き V1 の既知の限界。
- **(M1) スキャン中に届いた再要求が捨てられていた**: rename 直後の再スキャン要求が in-flight スキャンに
  飲まれ、ドロワーが旧名のまま残る。`_sessionsScanPending` フラグで「終わったらもう1周」に変更。
- **(M2) タブリネームの prefill が表示ラベルだった**: 自動タイトル表示中にダブルクリック → blur で
  そのタイトルが**恒久的な手動名として確定**してしまう（以後 ai-title の更新を影で塞ぐ）。
  `customName` を `onTabCreated`/`onTabLabel` に同梱し、input は手動名のみ prefill・自動タイトルは
  placeholder に（ドロワー側と同じ規約に統一）。
- **(M3) 描画 push が編集中のリネーム input を破壊**: 他タブの終了などで届く `onSessionsList` が
  `list.textContent = ''` で focused input を消す（WebKit は blur も発火しない＝入力が無言で消失）。
  input が開いている間は再描画をスキップ。
- **(M4) slug 変換が Unicode を残していた**: `char.IsLetterOrDigit` は日本語等を保持するが、claude の
  エンコーダは ASCII 英数字以外を全て `-` にする。日本語ユーザー名のホームパスでドロワーが常時空になる
  ところだった。ASCII 限定判定に修正。
- **(L1) `fs.Length` の二重読み**: スキャン中に claude がファイルを truncate すると負サイズ確保で例外
  （per-file catch で握るが）。長さを1回だけスナップショット。
- **(L2) Dispose 後の遅延スキャンが破棄済み WebView に触る**: `Dispose` で `_webReady=false` を立てて遮断。
- **(L3) session-names.json の非アトミック書き込み**: クラッシュで全名前消失し得る → temp + `File.Move`。

採用しなかった/受容した指摘: 巨大単一行（64KB 超・改行なし）のトランスクリプトは一覧に出ない（実運用で
発生せず、空セッションフィルタの副作用として無害）。フォールバックシェルタブの SessionId が
jsonl 化されない点は既知（リネームすると孤児エントリ、無害・剪定は roadmap）。

### Codex レビューの指摘と対応（同日・2回目）

- **(HIGH) 起動中タブ close 後の stale `EnsureSession`**: `Starting` 中のタブを閉じるとスロット claim が
  即解放され、再割当された新タブの `.mcp.json` を、まだ走っている旧タブの `StartSession`（BG スレッド）が
  旧パネルのポートで上書きし得る——新 claude が**別の Rhino doc に接続**する H1 と同類の穴。
  対応: `UnregisterTab` は `Starting` 中なら claim を解放せず `ReleaseSlotOnLaunchExit` を立て、
  launch タスクの finally（`Starting=false` の後）が UI スレッド経由で解放する。
  claim が生きている限り再割当は起きないので、stale な書き込みは無主のディレクトリにしか届かない。
- **(LOW) 巨大な初回メッセージ（head チャンク超）の実セッションが一覧から消える**: head に user 行が
  見つからなくても tail の `last-prompt` が実在の証拠 → `HasContent` に `lastPrompt != null` を追加。
- **(LOW・受容) 他パネルのスロット解放がドロワーへ即時反映されない**: パネル B のドロワーを開いたまま
  パネル A がスロットを解放しても、B の一覧は次の契機（開き直し・自パネルのタブ増減・rename）まで
  「スロット使用中」のまま。resume の安全性は C# 側の `ClaimedSlots` 検証が担保しており表示だけの問題。
  パネル横断の push はパネルレジストリが必要になるため V1 では見送り（roadmap）。

## 追加決定 D11: 共有ワークスペース化（D10 のスロット方式を即日改訂）（2026-07-07）

**意図**: D10 のスロット方式（`sessions/<agentId>/` に履歴を蓄積し、resume はホームスロット限定）を
ユーザーに説明したところ「スロット使用中」という状態の存在自体が複雑すぎるという指摘を受けた。
振り返ると、占有概念（最小空き割当・`ClaimedSlots`・占有中 resume 不可・mid-launch の claim 遅延解放）は
すべて「cwd がスロット単位」という前提から派生した複雑さで、レビューで塞いだ衝突バグ2件（H1・Codex HIGH）も
この前提が生んだものだった。分離の実体は cwd ではなく (a) セッション ID（トランスクリプトは ID ごとの jsonl）と
(b) `.mcp.json` の中身（ポート）なので、**cwd を1つにすれば占有概念ごと消せる**。

### 判断と選択理由

- **cwd を `~/.corhino/workspace/` に一本化**: `--resume` の cwd 制約が「どのセッションもこの cwd に属する」
  ことで常に満たされ、resume はいつでも可能に。ブロック状態は「実行中」（同一トランスクリプトへの
  二重 attach 防止）のみになり、ユーザーに見える概念が「セッション」だけになる。
- **`.mcp.json` を cwd の外へ**（`~/.corhino/runtime/mcp-<port>-<agentId>.json`）: `--mcp-config` は
  明示パスを取るのでそもそも cwd に置く必然が無かった。ポートがパネル一意なのでパネル間衝突も構造的に消える
  （スロット方式で必要だった H1/Codex HIGH 対策のコードを全削除できた根拠）。
- **担当レイヤをセッション紐付けに**（D7 の割当単位を改訂）: 共有 cwd では per-dir CLAUDE.md にレイヤを
  焼き込めない。`--append-system-prompt`（interactive 対応をヘルプで確認）で spawn 毎に注入し、値は
  `s-<sessionId 先頭8桁>` を**決定的に導出**——`{sessionId → layer}` の index 永続化が不要で、resume でも
  会話が作業してきたレイヤがそのまま復元される。共有 CLAUDE.md はルールのみ持ち、値は持たない。
- **agentId は純粋なルーティングキーに戻す**: 単調連番（`_nextAgentNum`）を復活。ディスク上の何にも
  対応しないので、パネル間の重複も無害。`ClaimedSlots` は **`LiveSessionIds`**（プロセス全体の実行中
  セッション集合）に置き換え——二重 resume 防止はセッション単位の関心事になった。mid-launch close の
  遅延解放（Codex HIGH 対策）は同じ形でセッション keyed に移植（mid-spawn の claude と resume の
  同時 attach を防ぐ）。
- **旧 `~/.corhino/sessions/` は丸ごと放棄**（D10 の no-migration 方針を継続。スロット版は未リリース・
  未コミットなので実質影響なし）。

### 実装（Workspace.cs、CoRhinoPanel.cs、SessionsCatalog.cs、sessions-drawer.ts）

- `EnsureSession(port, agentId, layer)` → `EnsureWorkspace()` + `EnsureAgentMcpConfig(port, agentId)` に分割。
  CLAUDE.md テンプレは共有版（レイヤはシステムプロンプト参照と記載）。
- スキャンは単一 project dir に（スロット列挙・`agent-<int>` フィルタ削除）。`SessionInfo.Slot`・
  ドロワーのスロットバッジ・「スロット使用中」状態を削除。
- プロトコルは不変（`onSessionsList` の行から slot/slotBusy が消えただけ）。
- **検証**: `dotnet build` 0 警告 0 エラー・`tsc --noEmit` 0 エラー。実機確認は未（D10 と合わせて実施）。
  `--append-system-prompt` の interactive での実効（レイヤ遵守）は実機確認項目に追加。
