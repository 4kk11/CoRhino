# セッション管理 タスクリスト

設計: [`session-manager-design.md`](session-manager-design.md) / D10・D11（design-history.md）

## Phase 1 — セッション ID + spawn 配線

- [x] 1-1 Workspace.cs: `EnsureWorkspace()`（共有 cwd + CLAUDE.md seed）/ `EnsureAgentMcpConfig(port, agentId)`（runtime/ に spawn 毎生成）/ `SessionNamesPath`
- [x] 1-2 CoRhinoPanel.cs: `AgentTab.SessionId/Resume/CustomName`、`--session-id`/`--resume`、`--append-system-prompt`（セッション紐付けレイヤ `LayerFor`）

## Phase 2 — データ層

- [x] 2-1 SessionNames.cs（lock + write-through、atomic write、剪定なし）
- [x] 2-2 SessionsCatalog.cs（単一 project dir、トップレベル jsonl のみ、head/tail チャンクパース、空セッション除外、allowlist）

## Phase 3 — パネル配線

- [x] 3-1 `onTabCreated` ペイロード化（{agentId, sessionId, label, customName}）
- [x] 3-2 メッセージポンプ: `listSessions` / `resumeSession`（allowlist + 存在 + 非実行中 `LiveSessionIds` 検証）/ `renameSession`
- [x] 3-3 `RequestSessionsScan`（single-flight + pending 再スキャン）/ `SendSessionsList`（UI スレッド装飾）/ `PushTabLabel`
- [x] 3-4 mid-launch close の遅延解放（`ReleaseSessionOnLaunchExit`——mid-spawn の claude と resume の二重 attach 防止）

## Phase 4 — UI

- [x] 4-1 sessions-drawer.ts（🕘、一覧・相対時刻・実行中のみブロック・resume・✎ リネーム、編集中は再描画スキップ）
- [x] 4-2 main.ts: プロトコル追従、タブラベル dblclick リネーム（prefill=手動名のみ）、ドロワー排他（skills-drawer に `close()` 追加）
- [x] 4-3 セッション削除（`deleteSession`: allowlist + 非実行中ガード、jsonl + subagents dir + 名前を削除。UI は 🗑 2段階確認、live 行には非表示）

## レビュー

- [x] R-1 汎用レビューエージェント: 8/9 修正（詳細は D10 の「レビューの指摘と対応」。うちスロット占有系の対策は D11 で不要化）
- [x] R-2 Codex レビュー: HIGH（mid-launch close のレース → 遅延解放、D11 でセッション keyed に移植）と LOW（HasContent の last-prompt 漏れ）修正、LOW（他パネル解放の表示遅延）は V1 受容

## D11 改訂 — 共有ワークスペース化

- [x] D11-1 スロット方式（AllocateSlot / ClaimedSlots / ホームスロット制約 / スロットバッジ）を全廃、cwd を `~/.corhino/workspace/` に一本化
- [x] D11-2 レイヤをセッション紐付け（`s-<id先頭8桁>` + `--append-system-prompt`）に、CLAUDE.md テンプレを共有版に
- [x] D11-3 ドキュメント追従（README / architecture / design-history D11 / roadmap / 本 plans）

## Phase 5 — ドキュメント

- [x] 5-1 README.md / architecture.md / design-history.md（D10 + D11）/ roadmap.md

## Phase 6 — 実機確認（未・ユーザー作業）

- [ ] 6-1 新タブが `~/.corhino/workspace/` を cwd に起動し、ログの GUID と同名の jsonl が `~/.claude/projects/-Users-<user>--corhino-workspace/` にできる
- [ ] 6-2 会話 → `/exit` → 🕘 に一覧（タイトル・相対時刻）→ クリックで履歴ごと復活し、同じ jsonl が伸び続ける
- [ ] 6-3 実行中セッションが「実行中」でクリック不可、その他はタブ数に関係なくいつでも resume できる
- [ ] 6-4 レイヤ指示の実効: 新タブでオブジェクトを作らせると `s-XXXXXXXX` レイヤ配下に入る。resume 後も同じレイヤに作る
- [ ] 6-5 開いてすぐ閉じたタブが一覧に出ない（空セッションフィルタ）
- [ ] 6-6 タブラベル dblclick でリネーム → `~/.corhino/session-names.json` に永続 → `/exit` + resume で名前復元 → 空リネームで自動タイトルに戻る
- [ ] 6-7 ⚡/🕘 の排他 + 開閉でターミナルが refit される
- [ ] 6-8 `nc -l 10500` でポートをずらしても（10501 起動）旧セッションを resume できる
- [ ] 6-9 最終タブ `/exit` → CoRhino 再実行 → 新 sessionId の agent-1、旧セッションは一覧から resume 可
- [ ] 6-10 複数タブ並列（＋で2つ目）が正常起動し、各タブの .mcp.json（runtime/）が自パネルのポートを指す
- [ ] 6-11 削除: 🗑 → 削除? → 確定で一覧から消え、`~/.claude/projects/<slug>/` から jsonl（と `<id>/` dir）が消える。1クリック目だけなら3秒で元に戻る
- [ ] 6-12 言語追従: 日本語 Rhino で UI が日本語、英語 Rhino（TestToggleLanguage 等で切替→再起動）で UI・ダイアログ・新規 seed テンプレが英語になる
- [ ] 6-13 （Windows）slug エンコーディング・リネーム入力・`cmd.exe /c` 経由の `--resume`
