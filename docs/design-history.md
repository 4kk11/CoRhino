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

## B9: タブ追加直後のレイアウト崩れ — 初回 resize が spawn 中に消失（2026-07-07）

**症状**: mac でタブ（エージェント）を追加すると、claude の初期画面が 80 桁想定で描画され、
実際の xterm 幅（例: 50 桁）で強制折り返しされて崩れる。パネルを手でリサイズすると直る。

**原因**: レースコンディション。タブ追加時、UI は即座に xterm を作って実サイズの `resize` を
ポストするが、ホスト側では `LaunchSessionInBackground` がまだ spawn 前
（mac は `CliResolver.ResolveUnix` のログインシェル起動だけで数百 ms かかる）。
旧 `PtySession.Resize` は `_connection?.Resize(...)` だったため、接続前に届いた resize を
**無言で捨てていた**。その後 PTY は `StartAsync` のデフォルト 80x24 で生成され、claude は
80 桁として描画。パネルサイズが変わらない限り xterm の `onResize`（cols/rows 変化時のみ発火）は
二度と発火しないので、崩れたまま固定される。手動リサイズで直るのは、fit で cols が変わって
`resize` が再送され、その時点では接続が生きているから（SIGWINCH → claude 全再描画）。

パネル初回起動の1枚目のタブで顕在化しにくいのは逆のタイミングのため: WebView 自体の起動が遅く、
UI の ready → fit → resize が届く頃には spawn が完了していることが多い。

**修正**（PtySession.cs）: 最後に要求されたサイズを `_sizeLock` 下で記憶し、
(a) `StartAsync` は spawn オプション組み立て時に記憶済みサイズでデフォルトを上書き、
(b) spawn 完了直後にも記憶済みサイズを適用（spawn 中に届いた resize の回収）、
(c) `Resize` は記録＋接続があれば即適用。ロックのペアリングにより「接続が見えて直接適用」か
「記録されて StartAsync が適用」のどちらかに必ず倒れ、消失経路がなくなった。
ホスト側（AgentTab にサイズを持たせて StartAsync に渡す案)ではなく PtySession 内で閉じたのは、
「PTY のサイズはセッション自身の関心事」であり、呼び出し側全員（fallback shell 含む）に
同じ配線を強いないため。

**Windows も同罪**: レース自体はプラットフォーム非依存（`ResolveWindows` も `where` の子プロセス起動で
spawn 前に時間がかかる）で、修正は共通コード（PtySession）なので両 OS に効く。ただし Porta.Pty の
`Resize` は**失敗時に例外を投げる**（Windows: `ResizePseudoConsole` 失敗 → `InvalidOperationException`、
Unix: ioctl 失敗も同様）。UI からの resize 経路は `OnMessageReceived` の catch が受けるが、
spawn 直後の適用は `LaunchSessionInBackground` の catch-all（＝失敗時 RemoveTab）に落ちるため、
mid-spawn の Dispose 競合や一過性の ConPTY 失敗で**正常起動したタブごと削除**しかねない。
適用は best-effort として try/catch + ログで握る形にした。

**検証**: `dotnet build` 0 警告 0 エラー。実機確認（タブ追加直後に崩れないこと）は次回 Rhino 起動時。
Windows 実機（ConPTY での同挙動）も未確認・要実施。

## B10: 入力ボックス周りの崩れ — PTY 出力のチャンク独立デコードで UTF-8 が割れる（2026-07-07）

**症状**: B9 修正後もタブ追加直後、入力ボックス付近が崩れる。罫線の途中に「⑦⑦⑦」様の化け文字、
入力行や status 行の行頭に `──` の断片、プレースホルダ行が古いまま残る。

**調査**（実機、rhino-cli `execute-panel-js` + `stty size`）: PTY winsize は 63x44 で xterm と完全一致
（B9 は解消済み）。xterm の buffer をダンプすると、罫線行（`─`×63 のはず）が
`─`×7 + **U+FFFD×3** + `─`×53 = 論理 65 セルになっており、はみ出た 2 セルが次行へ折り返して
`──ああああ` `──⏵⏵ auto mode` の行頭断片を作っていた。ink（claude の TUI）は自分が描いた行数を
前提にカーソルを上げて再描画するため、想定外の折り返し 1 行で行勘定がずれ、古いプレースホルダ行が
消えずに残る。

**原因**: `OnPtyData` が `ReadLoop` の 4096 バイトチャンクごとに独立して `Encoding.UTF8.GetString` して
いた（io-flow の注意点に「設計上の弱点」と明記済みだったものが顕在化）。罫線 `─`(E2 94 80)×63 は
189 バイトの連続 3 バイト文字で、大きな再描画がチャンク境界を踏むと 1 文字が U+FFFD×2〜3 に割れ、
行が 1〜2 セル**伸びる**。「実用上顕在化しにくい」という当時の見立ては、化けが表示だけでなく
**レイアウト破壊**（行伸長 → 折り返し → ink の行勘定崩れ）に繋がる点を見落としていた。

**修正**: デコードを `PtySession.ReadLoop` 内の `Encoding.UTF8.GetDecoder()`（状態保持）に移し、
`DataReceived` イベントを `Action<byte[]>` → `Action<string>` に変更。部分バイト列は Decoder が
チャンク跨ぎで保持するので、文字は必ず 1 回・無傷で出てくる。バイト列の解釈（UTF-8）はストリームの
所有者である PtySession の関心事、という責務でも自然な置き場所。

**検証**: `dotnet build` 0 警告 0 エラー。実機確認（タブ追加直後に罫線・入力ボックスが崩れないこと、
日本語出力の化けが出ないこと）は次回 Rhino 起動時に B9 と合わせて実施。

### 追加: セッション削除（同日）

ドロワーから不要セッションを削除できるように `deleteSession` を追加。判断:

- **削除対象は claude のトランスクリプト実体**（`<id>.jsonl` + subagents の `<id>/` dir）+
  `session-names.json` のエントリ。CoRhino 側に独自の複製は無いので、これで一覧からも消える。
- **確認 UI は 2 段階クリック**（🗑 → 「削除?」→ 確定、3 秒で自動解除）。ネイティブ `confirm()` は
  埋め込み WebView（WKWebView/WebView2 の LoadHtml 経路）で描画が当てにならず、モーダルはメッセージ
  ポンプを塞ぐリスクもあるため使わない。
- **ガードは resume と同じ規律**: allowlist（直近スキャンの ID のみ）+ 実行中は拒否（UI 側も live 行には
  🗑 を出さない）。楽観削除はせず、ホストの再スキャン push で一覧が更新される（拒否時も自己修復）。

### 追加: Rhino 言語設定への追従（同日）

パネル UI・ダイアログ・claude への注入文を Rhino の言語設定に追従させた（ja/en の2択、
`AppearanceSettings.LanguageIdentifier == 1041` で判定）。判断:

- **OS ロケールではなく Rhino の言語**を見る（パネルはドック先のアプリと同じ言語で読めるべき）。
  Rhino の言語変更は再起動必須なので、判定はプロセス起動時に1回（`L10n` static readonly）。
- **UI への伝達は HTML 注入**（`LoadWebView` で `<head>` に `window.CORHINO_LANG`）: シェルは boot で
  同期的に DOM を組むため、`ready` 応答で言語を返すのでは間に合わない（一瞬別言語が見える or 再描画が要る）。
  単一 HTML を `LoadHtml` する構成だからこそ取れる最短経路。dev サーバ経路は注入不可 → ブラウザロケールに
  フォールバック（開発時のみ）。
- **seed されるテンプレ（CLAUDE.md・skills README/manifest）は seed 時の言語で固定**: 初回 seed 後は
  ユーザー所有で上書きしない既存方針に従う。言語を変えたければ削除して再 seed。
  spawn 毎に注入するレイヤ指示とダイアログは常に現在の言語。

## 追加決定 D12: ログの2レベル化 — コマンドラインエコーの原則廃止（2026-07-07）

従来 `Log.Write` は全メッセージをファイル追記 + `RhinoApp.WriteLine` の両方に出しており、
spawn トレース・env keys・セッションスキャン等の診断ノイズが Rhino コマンドラインを埋めていた。

- **`Log.Write` はファイル専用に**（`%TMPDIR%/CoRhino.log`）。診断はファイルを `tail` すれば足りる
- **`Log.User` を新設**: コマンドライン + ファイルの両方に出す。ダイアログもパネル UI も持たない
  ユーザー影響のある失敗だけに限定 — claude CLI 不在フォールバック、PTY 起動失敗、MCPSpawn 失敗、
  `web/index.html` 欠落、yak インストール進捗
- **切り分けの基準は severity ではなく「ユーザーがコマンドラインで見る必要があるか」**。エラーでも
  ダイアログで伝わるもの（onboarding 系）や自己修復するもの（セッションスキャン失敗等）はファイルのみ
- インストーラーの直接 `RhinoApp.WriteLine` も `Log.User` に統一し、コマンドライン出力の経路を一本化
  （ファイルログに漏れなく残る）

## 追加決定 D13–D18: Codex CLI 対応 — タブ単位のエージェント CLI 選択（2026-07-07）

計画・詳細設計は [`plans/codex-cli-support-design.md`](plans/codex-cli-support-design.md)、
タスク消化と検証注記は [`plans/codex-cli-support-tasks.md`](plans/codex-cli-support-tasks.md)。
ここには判断の要点と検証・レビューの経緯だけを残す。

### 意図

エージェント本体（CLI）は交換可能な部品であるべきで、claude 固有の配線がオーケストレータに
直書きされている状態は D2（責務境界）の精神に反する。タブ単位で Claude Code / Codex を選べるようにし、
その過程で CLI 差分を層として分離する。仕様はインタビューで確定: タブごとに選択（混在可）、
初回タブは常に claude、＋クリックのミニメニュー、段階的フル対応。

### D13: CLI アダプタ層 `IAgentCli`

- 起動引数（MCP 隔離・レイヤ指示注入）・セッション同定・スキル capability を CLI ごとの
  アダプタ（`src/Cli/`）に閉じ込め、`CoRhinoPanel` は CLI 非依存の PTY 配線だけを持つ。
  claude 経路も同時に移設（pre-alpha 方針: 互換シムを作らない）
- Phase 1 は「claude の挙動が 1 ミリも変わらない」を完了条件とするリファクタとして先行。
  レビュー指摘で `CliResolver` の結果キャッシュ案を撤回（キャッシュは「起動中に CLI を
  インストールしたら次の spawn で拾える」現行挙動を壊す）— 解決は毎 spawn、＋メニューの
  availability はパネル ready 時の BG プローブ 1 回

### D14: codex の隔離は `-c` 合成（`~/.codex` 無変更）

- スパイク（自作 streamable-HTTP MCP サーバ + `codex exec`、0.142.5）で確定した事実:
  `-c 'mcp_servers={...}'` は**全置換ではなくマージ**、`--ignore-user-config` は exec 専用、
  `http_headers` は全リクエストに到達、MCP ツール承認は `default_tools_approval_mode="approve"`
  （**approve = 事前承認済み・プロンプトなし**。当初 `auto` を採ったが auto は「codex が判断」で
  run_python 等にプロンプトが出る — 実機フィードバックで発覚し修正。approve なら exec モードでも
  tools/call が通るため、Phase 0 で「exec の upstream バグ #24135」とした現象も実は承認設定で回避可能だった）、
  `-c developer_instructions` が `--append-system-prompt` の等価物（追記・不可視・実測）
- 合成レシピ: `--disable plugins`（プラグイン注入サーバを一括排除）+ config.toml の実在
  `[mcp_servers.*]` を個別 `enabled=false` + rhino 追加 + `projects.<workspace>.trust_level`
  注入（初回 trust プロンプト抑止・永続化させない）
- config.toml の名前抽出は軽量パース。**不確実な名前は disable しない方向に倒す**
  （存在しない名前への enabled=false は起動を殺す — レビュー指摘で literal quoted key 対応・
  エスケープ不明時 skip+log・inline table 検出ログを追加）
- **承認ポリシー（同日追記・実機フィードバック起点）**: 当初は codex 既定（on-request）に任せたが、
  シェルコマンドごとの承認が実運用で煩雑 → `-a never -s workspace-write` を既定に。プロンプトなしで
  実行しつつサンドボックスは維持（書き込みは workspace+tmp、失敗はモデルに返る）。`--full-auto` は
  0.142.5 に存在せず `on-failure` も deprecated のため、この組が現行の「サンドボックス付き自動」。
  `--dangerously-*` は使わない
- **スキル置き場を writable root に追加（2026-07-08・実機フィードバック起点）**: `workspace-write` は
  cwd+tmp 以外への書き込みを拒否するため、`save-rhino-skill` の保存先 `~/.corhino/skills` に書けず
  codex セッションはスキルを「読めるが書けない」状態だった。workspace の symlink
  （`.agents/skills`）経由の書き込みも不可 — サンドボックスは実パスに解決して判定する
  （`codex sandbox` 0.143.0 でスパイク検証）。→ `-c sandbox_workspace_write.writable_roots=
  ["<UserSkillsRoot>"]` を合成に追加。パスはフォワードスラッシュで渡す（`\\` エスケープの TOML は
  配列としてパースされず literal string 扱いに落ちる事象を実測）。サンドボックス自体は維持
  （`--dangerously-*` 不使用の方針は不変）

### D15: codex セッション ID の事後発見と帰属保証

- codex は `--session-id` 相当を持たない → spawn 後に `~/.codex/sessions/` の新規 rollout
  （1 行目 `session_meta.cwd` == 共有 workspace、mtime ≥ spawn−5s）を突合して ID を確定。
  rollout は起動数秒内に生成される（実測）ため 500ms × 15s ポーリングで足りる
- **レビュー 3 ラウンドで設計が強化された箇所**（当初の claimed 集合だけでは不十分だった）:
  1. 全 codex タブが同一 cwd を共有するため候補 rollout の帰属を証明できない →
     process-wide の `CodexSpawnGate` で [起動 → claim] を直列化（未 claim の一致 rollout は常に高々 1 つ）
  2. タブ除去時に discovery を単純中断すると自 rollout が未 claim で残り次タブが誤 claim →
     cancel は **burn mode**（ゲート保持中に claim して捨てる。deadline は +4s 短縮）
  3. ID 未確定の実行中セッションが Sessions Drawer で非 live に見え二重 resume / live delete が可能 →
     `PendingCodexTabs` + 単一判定 `IsSessionBlocked`（live ∪ deleting ∪ pending-cutoff）を
     装飾と resume/delete ガードで共用。`codex delete` は実行中予約（`DeletingSessionIds`）
- Sessions Drawer は両カタログ（`SessionsCatalog` + `CodexSessionsCatalog`）のマージ 1 タイムライン。
  codex のタイトルは `session_index.jsonl` の `thread_name`（exec セッションが載らない遅延あり →
  ベストエフォート）> 先頭 `user_message`。削除は `codex delete --force <id>`
  （`--force` 必須: 非対話端末では確認プロンプトを出せず exit 1 — 実機で発覚し修正）

### D16: codex のレイヤは C# 採番 + 永続マッピング

- レイヤの決定的導出（D11: `s-<id先頭8桁>`）は spawn 時に ID が無い codex では不可能 →
  C# が `s-<8hex>` を採番して `-c developer_instructions` で注入し、ID 確定後に
  `session-layers.json` へ永続化。resume は復元（マッピング欠落時は採番し直して保存）

### D17: AGENTS.md を同一テンプレから seed

- codex は CLAUDE.md を読まない。並列エージェントルールは CLI 非依存なのでテンプレを
  `WorkspaceGuideTemplate` に共通化し、`EnsureWorkspace` が両ファイルを「無ければ seed」
  （以後ユーザー所有・symlink ではなく別ファイル = CLI ごとに育て分ける余地を残す）

### D18: codex のスキルは workspace symlink mount（2026-07-07・実機フィードバック起点）

- 当初「codex に `--plugin-dir` 相当なし」として Skills Drawer を codex で無効化していたが、
  サブエージェント調査（ローカル実機 + 公式ソース rust-v0.142.5）で codex のプロジェクトスキル
  発見規約が判明: `<cwd>/.agents/skills` と `<cwd>/.codex/skills` の両ルートを読む（symlink 可・
  trust 不要・`--disable plugins` と干渉なし・SKILL.md は claude 形式互換・
  symlink 先の `.claude-plugin/plugin.json` も認識し plugin 名前空間付きでロード）
- workspace に symlink 2 本（`.agents/skills` → ユーザーストア、`.codex/skills` → 同梱 cc-plugin）で
  **claude と同一のスキル実体を共有**。`~/.codex` は無傷
- 制約と対応: in-session reload なし（`SupportsSkillsReload=false` — codex タブでは ⟳ 非表示、
  ⚠ は「新しいタブから有効」）。ドロワーは mount 経由 CLI ではソースごとにリンク健全性
  （LinkTarget 照合）を検証してから列挙（レビュー指摘: リンク不成立時に「セッションに無いものを
  見せない」ため）。ユーザーのグローバルスキル（`~/.codex/skills` / `~/.agents/skills`）の混入は
  CLI から抑止できない既知の制約
- 「save-rhino-skill がない」の初報は誤検知も含んでいた: codex の全列挙応答が plugin 名前空間付き
  スキル（`corhino:save-rhino-skill`）を省略しており、ロード自体は成功していた

### D19: CLI 未解決タブはセットアップビュー（サイレントシェルフォールバック廃止・2026-07-07）

- 旧挙動: CLI が PATH に無いタブは黙って `bash -l` / `cmd.exe` にフォールバック。初見には
  「なぜかただのシェルが開いた」= 故障にしか見えず、理由はログを見ないと分からなかった
- 新挙動: PTY を起動せずタブ内に HTML セットアップビューを表示。公式ネイティブインストーラの
  ワンライナーを表示・コピー・**その場で実行**（そのタブの PTY に出力を流す）でき、exit 後の
  再プローブ成功で同じタブに CLI が起動する。手動導入向け「再チェック」、旧フォールバック相当の
  「かわりにシェルを開く」は明示操作として残置。詳細は
  [`plans/cli-onboarding-design.md`](plans/cli-onboarding-design.md)
- **npm ではなくネイティブインストーラを採用**（ユーザー指定 + 調査 2026-07-07）:
  claude = `curl -fsSL https://claude.ai/install.sh | bash`（公式第一推奨・自動更新付き）、
  codex = `curl -fsSL https://chatgpt.com/codex/install.sh | sh`（README 筆頭）。
  Windows は各 `install.ps1` を `powershell -Command` で。表示と実行は同一文字列
  （`IAgentCli.NativeInstallCommand`）で乖離させない
- **落とし穴（実測）**: 両インストーラは `~/.local/bin` に配置し PATH 追記は**ユーザーのログイン
  シェルのプロファイル**（zsh なら `.zprofile`/`.zshrc`）に行う。CoRhino のプローブは
  `/bin/bash -lc` なので導入直後でも `command -v` が外れる → `CliResolver` に `~/.local/bin/<bin>`
  直チェックのフォールバックを追加し、この経路で解決した spawn は `PATH` に同ディレクトリを前置
- インストーラ exit の成否判定は exit code ではなく**再プローブ**（「導入済みで exit 0」や
  「Ctrl-C したが本体は配置済み」があるため）。installer の exit は `InstallerActive` フラグで
  `OnSessionExited` から分岐し、タブ削除ではなく再プローブ→再起動フローへ
- 失敗時は全面オーバーレイではなく**下端バナー**（インストーラ出力を診断のため読めるまま残す）
- CLI 起動成功時に `PushCliAvailability` を再送し、＋メニューのグレーアウトもパネル再起動なしで解消
- Codex レビューゲート: Medium 3 件（relaunch 時 resize 再送漏れ / `PushCliSetup` の stale-tab
  チェック欠落 / setup 状態 codex タブの pending guard 保持）を修正 → LGTM（詳細は
  [`plans/cli-onboarding-design.md`](plans/cli-onboarding-design.md) のレビュー節）

### 検証・レビュー体制（このフェーズ群で確立したこと）

- 各 Phase 末尾に Codex レビューゲート（LGTM まで反復）。計 5 フェーズで High 指摘 4 件・
  Medium 3 件を実装前に検出・修正（resolver キャッシュ、帰属レース×2、TOML パース穴、
  live ガード×2、delete 中 resume レース）
- codex 側の挙動は「公式 docs / ソース」と「ローカル実機」の両面で裏取りしてから設計に反映
  （例: `.codex/skills` ルートは docs の trust 条件記述と実測が食い違い、実測を採った）

## B11: Windows で codex タブが開いた瞬間に閉じる — .cmd shim 解決が node.exe を実体と誤認（2026-07-08）

**症状**: Windows で codex タブを開くと PTY が即 exit（code 9）してタブが消える。
`codex delete` 経由のセッション削除も同根で失敗。mac では問題なし。

**原因**: 旧 `ResolveCmdShimTarget` は「shim 内で最初に引用される `.exe` = 実体」とみなしていたが、
npm 標準の node-script 形 shim（codex.cmd）で最初に引用される `.exe` は
`IF EXIST "%dp0%\node.exe"` の **node.exe**。fnm 環境では shim と同じディレクトリに node.exe が
実在するため `File.Exists` も通り、パネルは「codex の引数を渡した bare な node.exe」を spawn
していた（node が未知の引数で即終了 → exit 9）。claude.cmd は `claude.exe` を直接引用する
direct-exe 形だったため偶然無事だった — つまり旧ロジックは claude の shim 形状にしか正しくなかった。

**修正**: `ResolveCmdShim` に形状判別を実装（`CliResolver.cs`）。
- direct-exe 形: **node.exe 以外の**最初の引用 `.exe` を実体として直接起動
- node-script 形: 引用された `.js` を検出し、**`node.exe` + script を prefix arg** に解決。
  `Resolve` の戻り値を `(cliPath, prefixArgs, loginPath)` に拡張し、spawn 側
  （`StartSession` / `CodexSessionsCatalog.Delete`）が CLI 引数の前に prefixArgs を挿む
- どちらにも解決できない shim は従来どおり `cmd.exe /c` フォールバック

**検証（Windows 実機・fnm + npm i -g @openai/codex = 0.132.0）**:
`node.exe codex.js` + BuildArgs 同等の全引数（`-a never -s workspace-write --disable plugins`、
`-c mcp_servers.rhino={...}`（http_headers / default_tools_approval_mode 含む）、
`-c projects.….trust_level`、`-c developer_instructions=…`）で codex が正常起動・応答することを
exec モードで確認。引数の引用符は Porta.Pty の Windows 引数整形（各引数をクォートし `"`→`""`）で
無傷に通る（デコンパイルで確認）。

**残課題（コードでなく環境）**: `codex delete` サブコマンドは 0.132.0 に存在しない
（0.142.x で追加）。設計のスパイク検証は 0.142.5 — 古い codex ではドロワーからの削除が
graceful に失敗する（行が残るだけ）。`codex update` / `npm i -g @openai/codex@latest` で解消。

## B12: Windows でセットアップビューの「インストール」が機能しない — PTY 環境と再プローブの2欠陥（2026-07-08）

D19 のセットアップビューは mac で実装・検証されたが、Windows 監査（静的解析 +
Porta.Pty 逆コンパイル + 両 install.ps1 の実読）で「インストール」ボタンの経路に 2 つの
欠陥が見つかった。配線（検出 → オーバーレイ → 再チェック → シェル）自体は OS 非依存で無事。

**欠陥 1: PTY の allow-list が unix 変数のみで、Windows の子プロセスは実質 `PATH`+`TERM` だけ**

- Porta.Pty の Windows 実装は `PtyOptions.Environment` を**マージせずそのまま** `CreateProcessW`
  に渡す（逆コンパイルで確認）。allow-list（`HOME USER LOGNAME …`）は全部 unix 名なので、
  Windows の子は `PATH`+`TERM` のみになる（実ログ `env keys: PATH,TERM` で確認）
- claude / codex 本体はこの最小環境でも動く（実機確認）が、インストーラは死ぬ:
  claude の install.ps1 は `$env:USERPROFILE`（DOWNLOAD_DIR）、codex の install.ps1 は
  `$env:LOCALAPPDATA`（配置先）に依存 — 空だとカレントドライブ直下への書き込みや
  `Join-Path` エラーになる
- 修正: allow-list を OS 別化し、Windows は標準システム変数
  （`USERPROFILE LOCALAPPDATA APPDATA TEMP SystemRoot ComSpec PATHEXT …`）を通す。
  セッション内サブプロセス（git の `~/.gitconfig` 等）の挙動も正常化する副次効果

**欠陥 2: codex の再プローブが Windows の公式配置先を見ない**

- codex の install.ps1 は `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`（バージョンストアへの
  junction）に配置し、PATH は**レジストリのユーザー PATH にのみ**追記する。起動中の Rhino の
  プロセス PATH は変わらないので `where.exe` は永遠に外れ、旧 well-known フォールバックは
  `~/.local/bin` しか見なかった → **インストール成功でも「failed」バナー、Rhino 再起動まで直らない**
- claude は配置先が `%USERPROFILE%\.local\bin` で旧フォールバックが偶然拾っていた（B11 の
  「claude だけ偶然無事」と同型 — 検証が claude に偏ると Windows の codex 経路が抜ける）
- 修正: well-known 配置先を CLI 固有知識として `IAgentCli.WellKnownBinaryPaths` に移し、
  `CliResolver.Resolve` はアダプタを受け取ってその候補を順にプローブする。
  plans/cli-onboarding-design.md の「codex も ~/.local/bin」という前提は Windows では誤りだった

**未検証のまま残る点**: powershell.exe + `irm` が新 allow-list 環境で完走するかは Windows
実機のセットアップビュー経由で要確認（CLI をアンインストールしないと再現できないため、
次に未導入マシンを触る機会に検証する）。

## B13: Windows の codex タブで日本語入力中に激しいちらつき — IME アンカーが render ごとにカーソルへ追従（2026-07-08）

**症状**: Windows で codex がストリーミング応答中（Working スピナー表示中）に日本語を打つと、
変換中テキストと IME 候補ウィンドウがペイン内を毎フレームテレポートし、画面全体が
ちらついて見える。mac では発生しない。

**解析**: 録画のフレーム間差分（codex ペインを crop して tblend=difference で数値化）で、
ちらつきは IME 合成中の区間にのみ集中し、転写領域そのものはほぼ無変化と判明。連続フレームの
目視で、xterm の composition view（変換中テキストのオーバーレイ）と OS の IME 候補ウィンドウが
左端→中央→左下と飛び回っていた。

**原因**: xterm.js（5.5）は合成中、`CompositionHelper.updateCompositionElements` を **`onRender`
のたびに**呼び、composition view と helper textarea を「その瞬間のバッファカーソル位置
（buffer.x/y）」へ inline style で再アンカーする。IME 候補ウィンドウは textarea のキャレットに
追従するので、カーソルが動けば候補窓も動く。codex は Working 中スピナー/転写のストリーミング
再描画でカーソルが画面中を移動し、特に ConPTY は独自のカーソル振り付けで再描画ストリームを
出すため、render に中間カーソル位置が大量に露出する — 合成中アンカーが毎フレーム別の場所へ
飛ぶ。mac は ConPTY 層がなくアプリのカーソル制御（隠して描いて入力欄へ戻す）がほぼそのまま
届くため顕在化しない。

**修正**: Windows 限定 CSS（B4 の 1×1 クランプと同じブロック）で `.composition-view` と
`.xterm-helper-textarea` の位置を **bottom-left に `!important` 固定**。両 CLI とも入力欄は
下端にあるので、候補窓は「打っている場所のそば」に安定して出る。xterm は毎 render に
inline の left/top を書くが、`!important` が常に勝つ。mac は既存スタンスどおり触らない
（クランプ系は WKWebView でキー欠落を誘発した前歴あり — B4）。

## B14: Windows の codex 稼働中に TUI 全体がちらつく — xterm.js が synchronized update 未実装（2026-07-08）

**症状**: B13（IME アンカー固定）後も、codex が応答生成中（スピナー/ストリーミング）の再描画で
ペイン全体がちらつく。mac では目立たない。

**解析**: Porta.Pty を参照する ConPTY キャプチャプローブ（scratchpad で codex TUI を 8 秒起動し
マスター側バイト列をダンプ）で実測:
- ConPTY 出力に **`CSI ?2026h` / `?2026l`（DEC synchronized update）が対で含まれる** —
  ratatui 系 TUI（codex）と現代の conhost は再描画 1 フレームをこのマーカーで括って出す
- しかし **xterm.js 5.5 は mode 2026 を実装していない**（バンドルに痕跡ゼロ）→ マーカーは無視され、
  フレームが出力チャンク（ReadLoop 4096B → AsyncInvoke → ExecuteScriptAsync 単位）を跨ぐと
  「行クリア済み・再描画前」の中間状態が xterm の render に露出する。ConPTY は再描画が多弁
  （8 秒のアイドルで EL×144）なので、Windows で特に激しく見える

**修正**: UI 側 `writeSynced`（main.ts）でフレームを組み立ててから渡す。`?2026h` を見たら
バッファ開始、対応する `?2026l` までを溜めて**完成フレームを 1 回の `term.write`** でアトミック適用。
- 閉じマーカーが来ない場合は 80ms タイムアウトで強制フラッシュ（プロセス死亡・マーカー破損でも
  出力が詰まらない）
- マーカーがチャンク境界で割れるのは、末尾の「マーカー接頭辞と部分一致する tail」を持ち越して吸収
  （ESC 単体で止まった非フレーム出力もタイムアウトで排出）
- マーカー自体は素通し（xterm は未知モードを無視するので無害）。単独の `?2026l` も素通し
- タブ削除でタイマー破棄、セッション再起動で組立途中のフレームを破棄（新セッションへ再生しない）
- 状態機械は node で単体検証済み（チャンク跨ぎ / 分割マーカー / 複数フレーム / ESC 末尾 / 単独 END）

**採らなかった案**: C# 側のチャンク結合（フレーム境界を知らないので不完全）、xterm の
registerCsiHandler（parse 時点では既に描画パイプラインに載っており「保留」ができない）、
xterm のアップグレード（5.5 が最新、2026 対応は upstream 未実装）。

## B15: 変換中テキストが確定（Enter）まで見えない — B13 の固定が画面外に解決されていた（2026-07-08）

**症状**: B13 適用後、claude / codex 両タブで日本語入力の未確定テキストがどこにも表示されず、
Enter で確定して初めて入力欄に現れる。候補ウィンドウ自体は安定して表示される（B13 の狙いは達成）。

**原因**: B13 は `.composition-view` と helper textarea を `bottom: 8px`（absolute）で固定したが、
両者の親 `.xterm-helpers` は **ペイン上端にある高さ 0 のコンテナ**（xterm.css: `position:absolute;
top:0`、height 指定なし）。高さ 0 の箱に対する `bottom: 8px` は「上端より 8px+要素高ぶん上」に
解決され、変換中テキスト（composition view）が描画領域の外に出ていた。candidate window は
Chromium がアンカーをビューポート内にクランプするため見えており、欠けたのはエコーだけ —
「候補は出るのに打った字が見えない」という症状の非対称はこれ。

**修正**: `position: fixed !important`（ビューポート基準）に変更。WebView のビューポート＝パネル
全体なので bottom-left 固定で常に可視、かつ B13 の「テレポートしない」性質は維持。
composition view と textarea を同一ルールに統合し z-index を明示。

**教訓**: inline style を CSS で上書きする際は、値だけでなく**包含ブロック**（positioned ancestor の
ジオメトリ）まで確認する。B13 の実機確認は「候補ウィンドウが飛ばない」ことだけを見て、
「変換中テキストが見える」ことを見ていなかった — IME 修正の確認項目は
①候補窓の位置安定 ②未確定テキストの可視 ③確定文字列の到達、の 3 点セット。

## B16: IME アンカーの隅固定を撤回 — カーソル追従に戻す（B13/B15 の最終形・2026-07-08）

**症状（B15 の後）**: 変換中テキストと候補ウィンドウがパネル左下の固定位置に出るため、
claude では実際の入力行（会話量に応じて上下する）から遠く離れ、下端の UI に候補リストが被る。
codex でも入力行とアンカーの位置が一致しない。「入力位置のそばに出る」直感性が失われていた。

**判断**: そもそも xterm のカーソル追従（`updateCompositionElements`）は**正しい位置**
（CLI の入力行）にアンカーしていた。B13 で問題だったのは追従自体ではなく、ConPTY 再描画フレームの
**中間カーソル位置**が render に露出してアンカーがテレポートすること。B14 の `writeSynced` が
フレームをアトミック適用するようになった時点で、render の見るカーソルは常に「フレーム末尾で
入力欄に戻った」位置になり、追従のままで安定する。プローブ実測で **claude（Ink、2026 非対応）の
ConPTY 出力にも `?2026h/l` が付いている**ことを確認 — conhost が全アプリのフレームを括るので、
両 CLI ともフレーム組立の恩恵を受ける。

**修正**: B13/B15 の位置 CSS を撤去（B4 の 1×1 サイズクランプは別問題なので残す）。
位置の安定は B14 のフレーム組立が担う。

**教訓**: 「症状の場所」（アンカーが飛ぶ）と「原因の層」（中間フレームの露出）を取り違えると、
対症療法（隅固定）が別の退行（B15・B16）を生む。層の切り分けは
症状の観察 → バイトストリーム実測（プローブ）→ ライブラリ実装の確認、の順で早めにやる。

## B17: codex だけ候補ウィンドウがまだ飛ぶ — フレーム末尾カーソルの置き去り（B13 系の完結・2026-07-08）

**症状（B16 の後）**: claude は安定したが、codex の応答生成中に日本語を打つと候補ウィンドウが
依然テレポートする（movie56 のフレーム解析: 転写領域は不変、候補窓だけが毎フレーム移動）。

**原因**: B14 のアトミック適用で「フレームの中間状態」は消えたが、**フレーム完成時点のカーソル
位置そのものが codex では毎フレーム違う**。ratatui（codex）は Working 中カーソルを隠したまま
描画終端に置き去りにするのに対し、Ink（claude）はフレーム末尾で必ず入力欄へ戻す — 「codex だけ」
の理由はこの TUI フレームワークの流儀の差。カーソル追従アンカー（B16）は正しいフレームだけを
見ていても飛ぶ。

**修正**: 合成単位のアンカー凍結。compositionstart（カーソルが確実に入力行にある唯一の瞬間）で
カーソルセルを `--ime-x/y` CSS 変数としてペインに記録し `.composing` クラスを付与、
`.composing .composition-view / textarea` の left/top を `!important` でその値に固定。
compositionend で解除。座標は xterm が inline で書くのと同じ「セル × css cell 寸法」なので
ピクセル一致。cell 寸法は private（`_core._renderService.dimensions.css.cell`）から取得し、
内部形状が変わったら凍結スキップ（素の追従にフォールバック）。

**残る妥協**: 合成中に入力行自体が動く（転写が伸びて入力欄が下がる）と、その合成が終わるまで
アンカーが数行ズレる。テレポートよりはるかにまし、として受容。

**B13 系のまとめ**: 隅固定（B13）→ 包含ブロックの罠で画面外（B15）→ 追従復帰（B16）→
codex の置き去りカーソルに敗北 → 合成単位凍結（B17、最終形）。

## 追加決定 D20: エージェントステータス表示 — CLI フック + ステータスファイル（2026-07-09）

**意図**: 並列タブ運用で「どのエージェントが動いていて、どれが止まっているか」がタブを開かないと
分からない。特に**承認待ちで止まっている**エージェントに気づけないのが実害。タブとセッション一覧に
3状態（稼働中 busy / 承認待ち waiting / 待機中 idle）のドットを出し、非アクティブタブの完了は
未読（緑）として開くまで残す。

### 検知方式の判断

- **PTY 出力ヒューリスティックは不採用**: アイドル時の再描画（スピナー・カーソル系）で誤検知する。
  正確なイベントソースが両 CLI にあるのに近似を選ぶ理由がない。
- **claude = 公式フック**（`UserPromptSubmit`/`PreToolUse`/`PostToolUse` → busy、
  `Notification(permission_prompt)` → waiting、`Stop`/`SessionEnd` → idle）。
  注入は spawn ごとに生成する settings JSON を `--settings` で渡す — `~/.claude` を汚さない D3 と
  同じ立場で、settings 経由のフックは信頼確認プロンプトが出ない。
- **codex = 同型フック**（0.142 系で claude 同型の hooks が入った。`PermissionRequest` → waiting、
  ペイロードは stdin 渡し）。当初案は「notify + Enter 検知の近似」だったが、調査でフック実装を確認し
  対称設計に変更（ユーザー確認済み）。注入は `-c hooks={...}` のインライン TOML。
  未信頼フックの承認は `--dangerously-bypass-hook-trust` でスキップ — CoRhino 自身が注入する
  フックをユーザーに vouch させる意味がないため。**トレードオフ**: フック未対応の古い codex は
  未知フラグで起動失敗する = 最新 codex 必須（pre-alpha 方針によりフォールバック経路は作らない）。
- **伝達はステータスファイル**（`~/.corhino/runtime/status/<port>-<agentId>`）: フックが状態語を
  echo で書き、C# の FileSystemWatcher（skills watcher と同パターン、ただしデバウンスなし＝
  レイテンシ優先・ファイル単位読み）が拾って `onTabStatus` を push。設定にパスを焼き込むので
  session_id のパースが不要（codex の ID 事後発見とも無縁）。

### Windows の要点

- claude のフックが Windows で cmd.exe か Git Bash (sh) で走るかは**公式に不確定** →
  コマンドを両シェルで同義の**ポリグリフ** `echo <state>>"<C:/forward/slash/path>"` にした
  （`>` 前にスペースを入れない = cmd の末尾スペース混入防止。読み側は Trim() が契約）。
- codex は unix=`sh -lc` / win=`cmd.exe /C` が仕様で確定しており、同じポリグリフ 1 本で
  `commandWindows` の出し分けすら不要。
- notify を使わなかった副次的理由: notify は argv 末尾に JSON を渡す直接 spawn（シェルなし）で、
  Windows では .cmd/.bat の BatBadBut 系エスケープや PowerShell の引数再パースが地雷。
  フックは stdin 渡しなので quoting 問題ごと消える。

### 既知の穴と補完

- **claude は Esc 中断で `Stop` が発火しない**（公式仕様）→ C# の stdin ルーティングで
  「ちょうど `\x1b` 1 文字 かつ 非 idle」なら idle に落とすフォールバック（矢印キーは
  `\x1b[A` 等の複数文字メッセージなので誤爆しない）。誤って落としても次のフックイベントが訂正する。
- 承認拒否後は次イベントまで waiting 表示が残る（数秒で収束、許容）。
- セッション一覧の status は**自パネルのタブ分のみ**（`_tabs` はパネルローカル）。他パネルの
  live 行は従来どおり汎用「実行中」にフォールバック。
- 各フック `timeout: 10`（echo は即時、ハング保険のみ）。

**検証**: `dotnet build` 0 警告 0 エラー（tsc + vite 込み）。実機（mac/Windows）確認は未。

### 改訂: ドット → グリフ（同日・実機フィードバック起点）

初版はタブに丸ドット（●）を出したが、CLI バッジ（● claude）と同形で区別しづらいという
フィードバックで即日改訂。状態ごとに**別グリフ**へ: `✻` アンバー回転 = busy（claude 本体の
スピナーと同じ語彙）/ `‼` 赤点滅 = waiting / `✓` 緑 = 完了未読 / **idle は非表示**
（静かな状態は無印 — タブバーのノイズも減る）。グリフ語彙は `statusGlyph()`（sessions-drawer.ts、
ドロワーとタブバーで共用）、見た目は `.statusglyph` CSS（main.ts）。検討した代替は
タブ下線バー（ブラウザ風）とバッジ加工（リング/グロー）— ユーザー選択でグリフ切替に決定。

## B18: Shift+Enter が改行ではなく送信になる — xterm の Enter に修飾キー区別がない（2026-07-09）

**症状**: パネル内の claude / codex タブで Shift+Enter を押しても改行されず、そのまま
メッセージが送信される。macOS のスタンドアロン端末（iTerm2 等）ではよくある問題だが、
CoRhino は自前の WebView 内 xterm.js なので端末側の制約とは無関係——原因は自分のコード側にある。

**原因**: xterm.js の既定キー処理は Enter を修飾キー無視で `\r` に変換する（Shift 有無を
区別しない）。`onData` が唯一の入口（[io-flow-xterm-pty.md](details/io-flow-xterm-pty.md)）で、
そこに至るまでのどこにも Shift+Enter 専用の分岐がなかった。既存の macOS 早取りワークアラウンド
（`main.ts` の capture-phase `keydown`）は印字可能キー（`key.length === 1`）だけを対象にしており
`"Enter"` は対象外なので無関係。

**対応**: `createTerminal()` で `term.attachCustomKeyEventHandler()` を使い、Shift+Enter の
keydown だけを横取りして `term.input('\n', true)` を呼ぶ（他のキーは既定処理へ通す）。
`\n`（Ctrl+J と同じ LF バイト）を選んだ理由は、claude・codex どちらも既にこのバイトを
「改行挿入」として扱っているため——新しいプロトコルを発明せず既存の挙動に乗せた。
`term.input()` を使うのは、既存の macOS ワークアラウンドと同じ経路（`onData` 一本化）を保つため。
IME 変換確定用の Enter は `composing` / `e.isComposing` で除外し壊さない。
`e.preventDefault()` を明示しているのは、`attachCustomKeyEventHandler` が `false` を返しても
xterm 自身が代わりに `preventDefault` してくれるわけではなく、ブラウザ既定の textarea 改行挿入が
素通りすると `input` イベント経由で二重送信し得るため（macOS には `swallowInput` の安全網があるが
Windows/WebView2 は既定経路のままなのでここで確実に止める必要がある）。

**検証**: `tsc --noEmit` / `dotnet build` とも 0 エラー。実機（mac/Windows）確認は未。
