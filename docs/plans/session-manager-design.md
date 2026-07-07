# セッション管理 設計書（D10 + D11: 共有ワークスペース + Sessions Drawer + タブ命名）

Status: 実装済み（2026-07-07）・実機確認待ち。判断の経緯は design-history.md D10/D11、現在の構造は
architecture.md（§2.3 / §2.4 / §2.9 / §3.2）を正とする。本書は設計時の要求とスコープの記録。

D10（スロット dir 方式）は実装当日に D11（共有ワークスペース）で改訂された。理由: 「スロット使用中」等の
占有概念が複雑すぎるというユーザー指摘。分離の実体は cwd ではなくセッション ID と `.mcp.json` の中身なので、
cwd を1つにすれば占有概念ごと消せる。詳細は design-history.md D11。

## 1. 目的

- タブを閉じる / Rhino を落とした後に**会話へ戻れる**ようにする（claude の cwd キー履歴 + `--resume` に乗る）
- 過去セッションの**一覧・閲覧**（Sessions Drawer、タイトル・日時）
- **タブ命名**（手動名 > claude 自動タイトル > agent-N、resume 後も引き継ぐ）

スコープ外（ユーザー決定）: パネル再オープン時のタブ構成自動復元。

## 2. 前提（claude CLI 2.1.202 実測）

- トランスクリプト: `~/.claude/projects/<cwd-slug>/<sessionId>.jsonl`。slug は cwd の ASCII 英数字以外を `-` 置換
- `--resume <id>` は**同じ cwd からのみ**有効 → cwd を全セッションで1つに固定すれば常時 resume 可能
- `--session-id <uuid>` で spawn 時に ID を固定できる（タブ⇄トランスクリプト対応の決め手）
- `--append-system-prompt` は interactive でも使える（ヘルプに print 限定の注記なし）
- タイトルは `"type":"ai-title"` 行の最後の出現（`summary` 行はもう無い）。fallback: `last-prompt` → 最初の非メタ user 行
- spawn しただけで jsonl が生成される → 空セッションフィルタ必須
- サブエージェントは `<sessionId>/subagents/` サブ dir に隔離 → トップレベル `*.jsonl` のみ列挙

## 3. 設計の骨子（D11 反映後）

1. **cwd は全セッション共有の `~/.corhino/workspace/`**。CLAUDE.md はルールのみ（レイヤ値なし）を seed。
   旧 `~/.corhino/sessions/` は移行せず放棄。
2. **`.mcp.json` は cwd の外**（`~/.corhino/runtime/mcp-<port>-<agentId>.json`、spawn 毎に上書き）。
   port がパネル一意なのでパネル間衝突は構造的に起きない。
3. **セッション識別**: 新規 spawn は `--session-id <GUID>`、resume は `--resume <id>`（fork しない）。
4. **担当レイヤはセッション紐付け**: `s-<sessionId 先頭8桁>` を決定的に導出し `--append-system-prompt` で注入。
   resume でも同じレイヤが復元される。index の永続化は不要。
5. **agentId は純粋なルーティングキー**（単調連番）。実行中セッションの集合はプロセス全体の
   `LiveSessionIds`（static、UI スレッド専有）が持ち、二重 resume を拒否。起動中に閉じられたタブは
   launch タスク静止まで live 扱いを維持（遅延解放）。
6. **SessionsCatalog**（SkillsCatalog と同じ規律）: BG スレッドで単一 project dir をスキャン、生の事実のみ
   返す。直近スキャンの allowlist が `resumeSession` をゲート。live 装飾は UI スレッド。
7. **命名**: `~/.corhino/session-names.json` `{sessionId: name}` を C# が所有（atomic write）。
8. **プロトコル**: `listSessions` / `resumeSession` / `renameSession` ⇄ `onSessionsList` / `onTabLabel`、
   `onTabCreated` は `{agentId, sessionId, label, customName}` ペイロード（破壊的・シム無し）。
9. **UI**: sessions-drawer.ts（skills と排他）。ブロック状態は「実行中」のみ。タブラベル dblclick リネーム
   （prefill は手動名のみ、自動タイトルは placeholder）。

## 4. 既知の限界（V1 受容・文書化済み）

- `/clear` で sessionId が回転 → タブの記録 ID が stale（rename・レイヤ指示が旧 ID に付く）
- **クロスプロセスの二重 resume ガードなし**（`LiveSessionIds` はプロセス内のみ。Windows の 2 Rhino のみ該当。
  `session-names.json` は last-write-wins）
- **Windows の slug エンコーディング未検証**（`C:\` の `\`・`:`）→ Windows で有効と言う前の必須ゲート
- フォールバックシェル起動時の SessionId は jsonl 化されない（リネームすると無害な孤児エントリ）
- 巨大単一行（head チャンク超・改行なし・last-prompt も無い）のトランスクリプトは一覧に出ない
- セッション一覧のライブ更新なし（開閉・タブ増減・rename 契機のみ。他パネルの実行中解除も次の契機まで
  反映されない——安全性は C# 側検証が担保、表示のみの問題）
- レイヤ規約はアドバイザリ（従来どおり）。`--append-system-prompt` によるレイヤ遵守の実効は実機確認項目

## 5. 検証

ビルド: `dotnet build src/CoRhino.csproj` / `cd ui && npx tsc --noEmit`（いずれも pass、0 警告/0 エラー）。
実機チェックリストは [`session-manager-tasks.md`](session-manager-tasks.md)。
