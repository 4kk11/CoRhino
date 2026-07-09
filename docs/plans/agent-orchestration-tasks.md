# エージェントオーケストレーション タスクリスト

設計: [`agent-orchestration-design.md`](agent-orchestration-design.md)

進捗管理: 各タスク完了時に `- [ ]` → `- [x]`。フェーズ完了時にヘッダへ「✅ 完了」を追記。
検証はビルド（`dotnet build src/CoRhino.csproj`）+ tsc pass + Phase 7 の実機確認。

## Phase 1 — プロトコル基盤 ✅ 完了

- [x] 1-1 Workspace.cs: `CommandsDir` / `ResponsesDir` / `TabsManifestPath(port)` 追加（Directory.CreateDirectory 込み）
- [x] 1-2 AgentCommands.cs（新規・UI 非依存 static）: コマンド JSON パース + スキーマ検証（id/from/action 必須、エラー語彙 `invalid-request` / `unsupported-cli`）、レスポンス書き出し（temp+move）、stale スイープ（自ポート cmd 全削除・responses の 1h 超過分削除）
- [x] 1-3 CoRhinoPanel.cs: command watcher（FileSystemWatcher、`cmd-<自port>-` フィルタ、IOException 30ms 1回リトライ、AsyncInvoke → dispatcher スケルトン）。パネル構築時スイープ・Dispose での watcher 破棄

## Phase 2 — spawnTab / closeTab + マニフェスト ✅ 完了

- [x] 2-1 CoRhinoPanel.cs: `AgentTab.ParentAgentId` 追加、`SpawnTab(kind, parent, initialPrompt, permissionMode, name)` 拡張（name は CustomName 経由で SessionNames に永続）
- [x] 2-2 dispatcher: spawnTab 実行（`MaxTabs = 8` ガード → `tab-limit-reached`、claude 固定、レスポンスに agentId/sessionId/layer/statusFile/transcript を格納）
- [x] 2-3 dispatcher: closeTab 実行（`from` の子のみ → `not-your-child`、`RemoveTab` 流用）
- [x] 2-4 マニフェスト書き出し: `RegisterTab` / `UnregisterTab` / リネームで `tabs-<port>.json` を temp+move 全量更新。transcript パスは `SessionsCatalog.ProjectSlug` から導出。`Dispose` で削除
- [x] 2-5 ClaudeCli.cs / IAgentCli.cs: `SpawnContext.PermissionMode` 追加、`--permission-mode`（指定時のみ）、`--append-system-prompt` に自己同定（agentId / port / agent-commands パス / マニフェストパス）追記

## Phase 3 — TUI ready ゲート + sendPrompt ✅ 完了

- [x] 3-1 Workspace.cs: `EnsureClaudeStatusSettings` に `SessionStart` → `ready` フック追加
- [x] 3-2 CoRhinoPanel.cs: `ApplyAgentStatus` の `ready` 特例（`TuiReady=true`、表示は idle、キューをフラッシュ）
- [x] 3-3 CoRhinoPanel.cs: `SendPromptToTab`（bracketed paste `ESC[200~…201~` → 150ms → `\r`）+ `AgentTab.PendingPrompts` キュー + ready 前保持 + 15s タイムアウトフォールバック（配送 + ログ）
- [x] 3-4 dispatcher: sendPrompt 実行（子のみ / `target-not-running` 判定 / busy 中も配送）。spawnTab の初期プロンプトも同キュー経由に一本化

## Phase 4 — 承認 UI（タブ単位グラント） ✅ 完了 → ⚠ D22 で全撤去（2026-07-09）

実装後の再検討で「親の Bash 承認と二重・防御価値ゼロ・最大の遅延要因」と判断し、
グラント/バナー一式を撤去して非ブロッキング通知（spawn ごとの `Log.User`）に置換。
経緯は design-history D22。以下は撤去済みの記録:

- [x] ~~4-1 CoRhinoPanel.cs: `AgentTab.OrchestrationGrant`（未決/Granted/Denied）、未決コマンドの保留キュー、拒否後の即 `denied`（バナー再表示なし）~~
- [x] ~~4-2 プロトコル: C#→UI `onOrchestrationRequest({requesterId, label, action, permissionMode})`、UI→C# `orchestrationDecision({requesterId, allow})`~~
- [x] ~~4-3 main.ts + i18n.ts: 承認バナー（タブバー直下・非モーダル、要求元ラベル + アクション + 緩和モード強調、許可/拒否、日英文言）。文言はユーザー由来を含むため textContent 挿入~~

## Phase 5 — orchestrate-agents スキル ✅ 完了

- [x] 5-1 cc-plugin/skills/orchestrate-agents/SKILL.md: 自己同定 → 子作成（uuid・temp+move・echo 例・レスポンスポーリング 30s・読了後削除）→ 完了待ち（**busy を見てから idle** 規約）→ transcript tail 回収レシピ → rhino MCP 裏取り必須 → ルール（子以外に触らない・レイヤ指示を子プロンプトに含める・closeTab で片付け）
- [x] 5-2 スキル発動の説明文（description）調整: 「別のエージェントに作業を任せたい」「並列で作業したい」で発動する文言

## Phase 6 — ドキュメント追従 ✅ 完了

- [x] 6-1 architecture.md: 2章（AgentCommands / watcher / マニフェスト / ready ゲート / 承認）+ 3章データフロー + 8章設定一覧（agent-commands / tabs-<port>.json / ready 状態語）
- [x] 6-2 design-history.md: D21 として経緯（リフレクション実証 → MCP サーバ不採用の理由 → インタビュー決定 4 点）
- [x] 6-3 README.md: オーケストレーション機能の利用手順（承認バナーの説明）
- [x] 6-4 roadmap.md: 並列エージェント節の更新（V2 送り分: codex 子タブ / dumpScreen / グラント永続化）+ 実機確認項目の転記

## Phase 7 — Codex レビュー（3巡目のみ未消化）

- [x] 7-1 Codex レビュー1巡目 — HIGH3/MED3/LOW1 → 6件修正・1件受容（from 詐称=ローカル信頼、design-history D21 参照）
- [x] 7-2 Codex レビュー2巡目 — 昇格グラント周りに HIGH3（bool tier / 表示が最強でない / FIFO 崩れ）→ tier 序数（`GrantedTier`）に改修。他の1巡目修正は「塞げている」判定
- [x] 7-3 Codex レビュー3巡目（tier 改修の確認）— codex exec の2回連続ストールで未消化のまま、
  D22 でレビュー対象（tier/グラント機構）ごと撤去され**目的消滅**。2巡目レビューが塞いだ穴は
  すべて「グラント機構が在る前提での穴」のため、機構撤去で不成立

## Phase 8 — 実機確認（ユーザー作業）

- [x] 8-1 フルサイクル: 完走（2026-07-09 実機）。ただし初期プロンプト不配送を sendPrompt 再送で回避しての完走 → 提出確認ループを実装、8-2 で再検証
- [ ] 8-2 ready ゲート（再検証・修正後）: spawn 直後の初期プロンプトがロスしない — settle 600ms + 提出確認（busy 待ち + Enter 再送×2）実装後の確認。SessionStart フック不発時に 15s フォールバックが効く
- [ ] 8-3 通知（D22 で承認から変更）: spawn ごとに Rhino コマンドラインへ通知が出て、コマンドは承認待ちなしで即実行される。spawn でアクティブタブが切り替わらない（D23）— 子の完了は未読ドットで見える
- [ ] 8-4 スコープ: 兄弟・ユーザータブへの sendPrompt が `not-your-child` で弾かれる
- [ ] 8-5 ガード: 9 個目の spawnTab が `tab-limit-reached`
- [ ] 8-6 孤児: 親タブを × で閉じても子が走り続け、ユーザーが UI から操作できる
- [ ] 8-7 permissionMode: `acceptEdits` 指定がコマンドライン通知に警告付きで出て、子が承認待ちなしで編集する
- [ ] 8-8 片付け: closeTab でマニフェスト・status ファイルから消える。パネル閉鎖でマニフェスト削除
- [ ] 8-9 busy 中 sendPrompt が次ターンで処理される（claude の入力キューイングに乗る）
- [ ] 8-10 系譜表示（D24）: spawn 産セッションが履歴一覧で `⧉` バッジ + 「↳ 親名 から spawn」付きで出る。親を rename すると表示も追従、親を削除すると汎用文言になる。Rhino 再起動後も残る

## 実装順序

```
Phase 1（基盤）
  ↓
Phase 2（spawn/close + マニフェスト）
  ↓
Phase 3（ready ゲート + sendPrompt）← D20 フック基盤に依存
  ↓
Phase 4（承認 UI）
  ↓
Phase 5（スキル）← プロトコル確定後
  ↓
Phase 6（ドキュメント）→ Phase 7（Codex レビュー）→ Phase 8（実機確認）
```
