# CoRhino ロードマップ — 未実装・今後の作業

このドキュメントの責務は「**これから／まだやっていないこと**」の一元管理だけ。
- 完了した判断・検証・バグ記録（なぜ今この形か）は [`design-history.md`](design-history.md)。
- 現在の構造そのものは [`architecture.md`](architecture.md)。

WIP / pre-alpha のため、ここの項目は予告なく増減・並び替えされる。

## 統合・配信

- **D8 の実機確認**: ユーザースキル plugin（`~/.corhino/skills`）と同梱 cc-plugin（`save-rhino-skill`）の
  `--plugin-dir` 配線は実装済み・ビルド/validate pass だが、パネル内 claude での発動
  （「skills/ に SKILL.md を置く → 発動」「今の手順をスキルにして → save-rhino-skill が書き出す」）の
  実機確認が未了。
- **配布用スキルプラグイン（Platform 側）**: キュレーション済みの **Rhino 操作ノウハウ**のスキル・agents を
  Platform 側の plugin として配布し、CoRhino は 3 本目の `--plugin-dir`（または marketplace 経由）を
  足すだけにする。CoRhino に置けるのは自身の規約を教えるオンボーディングスキルのみ（→ D8 追記）。
- **cc-plugin の agents/commands ロード（旧 S3 / U1 の残）**: `--plugin-dir` の配線と skills 同梱は D8 で
  入った。agents/commands を CoRhino が定義した http `rhino` サーバに束縛できるかの実機判定が残り。
  現状は **agents 無し・素のツール直叩き**で E2E が成立している。
- **plugin 配信の更新戦略**: marketplace 自動更新か submodule バンプかを確定する。

## パネル UX

- **Skills Drawer の実機確認**: 実装済み（2026-07-07、→ D9 /
  [`plans/skills-drawer-tasks.md`](plans/skills-drawer-tasks.md) Phase 4）。ビルド・tsc は pass だが
  実機（Rhino 8）でのドロワー開閉/リサイズ・ライブ更新・⚠→⟳→● の遷移・openSkill が未確認。
  D8 の実機確認とまとめて行う。Codex レビューゲート（Phase 1–3）も codex の再ログイン待ちで未消化。
- **セッション管理の実機確認**: 実装済み（2026-07-07、→ D10/D11 /
  [`plans/session-manager-tasks.md`](plans/session-manager-tasks.md)）。ビルド・tsc は pass だが
  実機での確認（共有ワークスペースへの spawn・ドロワー一覧/resume・レイヤ指示の実効・空セッション除外・
  リネーム永続・ドロワー排他）が未了。チェックリストは tasks ファイル参照。
- **セッション管理の先送り分（D10/D11 の続き）**:
  - **Windows 対応ゲート**: `~/.claude/projects` の slug エンコーディング（`C:\` の `\`・`:`）が未検証。
    Windows で機能を有効と言う前に必須。
  - **`/clear` の ID 回転追跡**: タブの `SessionId` が stale になる（リネーム・レイヤ指示が旧 ID に付く）。
  - **タブ構成の自動復元**: パネル再オープン時に前回タブを `--continue` で復元（今回スコープ外と決定）。
  - **クロスプロセスの二重 resume ガード**: `LiveSessionIds` はプロセス内のみ。Windows の 2 Rhino から
    同一セッションを resume できてしまう。
  - **セッション一覧のライブ更新**: 現状は開閉・タブ増減・rename 契機のみ（watcher なし。
    他パネルの実行中解除も自パネルの次の契機まで反映されない）。
  - **session-names.json の剪定**: 消えたセッションの孤児エントリを掃除する仕組み。

## オンボーディング

- **claude CLI 未導入時の導線整理（旧 Phase 2b）**: 現状は `claude` が PATH に無いと
  `bash -l` / `cmd.exe` へ無言フォールバックするだけ。導入を促す UX を決める。
- **パネル内の接続ステータス／インジケータ（旧 Phase 2c）**: 「Rhino-MCP 未接続」等を
  パネル内に表示する。現状はネイティブダイアログ＋コマンドラインで代替している。

## 堅牢性

- **ポート枯渇時のフォールバック UX**: `Workspace.ResolveFreePort` は 10500–10599 を試行し、
  100 個すべて埋まると 10500 を返すだけ（後段の bind エラーで顕在化）。明示的な UX が無い。
  （※「10500 使用中なら採番」自体は D6 で実装済み。ここは“全滅時”の話。）

## 並列エージェント（D7 の続き）

1 doc に複数 claude（タブ並列）は実装済み（→ [`architecture.md`](architecture.md) 5.2 / D7）。残るのは衝突の
“強制”と相互可視化。現状はレイヤ接頭辞によるアドバイザリ分離のみ。

- **ホスト側アドバイザリ・リース／ロック**: `X-CoRhino-Agent` ヘッダをキーに、公式 Rhino-MCP-Platform 側へ
  `acquire_lease(scope)` / `release_lease` 相当のツールを追加し、同一オブジェクト/レイヤへの同時書き込みを
  防ぐ。**CoRhino 側にツールは置かない**（設計境界）。ヘッダ注入までは CoRhino 済み。実際に衝突が痛くなってから着手。
- **エージェント間の変更通知**: MCP は req/res のみで外部変更を push しない。レイヤ分離前提では不要だが、
  跨いで参照する用途が出たら検討（ホスト→クライアントの通知 or ポーリング規約）。
- **タブ UX の作り込み**: 用途別プリセット（builder / reviewer 等）、エージェント数の上限やポート負荷の表示。
  タブのリネームは D10 で実装済み（手動名 > 自動タイトル > agent-N）。
