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
- **ステータス表示の実機確認（D20）**: 実装済み（2026-07-09、フック方式・→ design-history D20）。
  ビルド・tsc は pass だが実機未確認: claude の busy/waiting/idle 遷移とタブドット、未読（緑）の
  点灯/解除、Esc 中断フォールバック、codex のフック発火と `--dangerously-bypass-hook-trust` の実挙動、
  セッション一覧 live 行の in-place 更新。**Windows 実機**では claude フックのシェル（cmd/sh どちらでも
  ポリグリフ echo が効くこと）とステータスファイル内容（CRLF/末尾スペースの Trim 吸収）を必ず見る。
- **サンドボックスの実機確認（D25）**: 実装済み（2026-07-10、→ design-history D25）。ビルド・tsc は
  pass だが実機（macOS）未確認: トグル ON → 新規タブで sandbox が実際に効くこと（cwd 外への書き込みが
  ブロック/フォールバックされる）、**status hooks（echo → ~/.corhino/runtime/status/）が sandbox 有効時も
  タブインジケータを駆動できること**（hooks が sandbox 対象かはドキュメント上不明確 —
  `allowWrite: ~/.corhino` でカバーされる想定の検証）、orchestrate-agents の drop-box 書き込み、
  save-rhino-skill のスキル書き出し、MCP（direct-HTTP localhost）が無影響であること。
- **ステータス表示の先送り分（D20 の続き）**:
  - 他パネルの live セッションは一覧で汎用「実行中」のまま（status はパネルローカル。
    プロセス内 static レジストリ化で解消可能だが V1 では見送り）。
  - 承認**拒否**直後は次のフックイベントまで waiting 表示が残る（数秒で収束、許容と判断）。
- **セッション管理の先送り分（D10/D11 の続き）**:
  - **Windows 対応ゲート**: `~/.claude/projects` の slug エンコーディング（`C:\` の `\`・`:`）が未検証。
    codex 分も同様: rollout パス・config.toml パースの Windows パス、および **workspace スキル symlink**
    （Developer Mode / 権限が無いと `CreateSymbolicLink` が失敗する — junction かコピーへのフォールバック要検討。
    現状は一度だけログして codex タブがスキル無しで動く）。CLI セットアップビュー（D19）の
    PowerShell インストーラ経路（`install.ps1` / `%USERPROFILE%\.local\bin` 直チェック）も未実測。
    Windows で機能を有効と言う前に必須。
  - **`/clear`（claude）/ `/new`（codex）の ID 回転追跡**: タブの `SessionId` が stale になる（リネーム・レイヤ指示が旧 ID に付く）。
  - **タブ構成の自動復元**: パネル再オープン時に前回タブを `--continue` で復元（今回スコープ外と決定）。
  - **クロスプロセスの二重 resume ガード**: `LiveSessionIds` はプロセス内のみ。Windows の 2 Rhino から
    同一セッションを resume できてしまう。
  - **セッション一覧のライブ更新**: 現状は開閉・タブ増減・rename 契機のみ（watcher なし。
    他パネルの実行中解除も自パネルの次の契機まで反映されない）。
  - **session-names.json / session-parents.json の剪定**: 消えたセッションの孤児エントリを掃除する仕組み。

## Codex CLI 対応の残り（D13–D18 の続き）

タブ単位の codex 対応（起動・MCP 隔離・レイヤ・Sessions Drawer・スキル mount）は実装済み
（→ [`plans/codex-cli-support-tasks.md`](plans/codex-cli-support-tasks.md) / design-history D13–D18）。残り:

- **グローバル codex スキルの混入抑止**: ユーザーの `~/.codex/skills` / `~/.agents/skills` は codex
  セッションに常に載り、CLI から抑止できない（無効化 API は app-server 専用）。隔離が claude より弱い
  既知の制約。codex 側に session-scoped な抑止手段が生えたら追従
- **codex セッションタイトルの改善**: `session_index.jsonl` は遅延・欠落する（exec セッションは載らない）。
  rollout からのタイトル抽出（先頭プロンプト以上のもの）を検討
- **初回タブ CLI の設定化 / 記憶**: 現状は常に claude（インタビューで決定）。codex 主体ユーザー向けに
  既定 CLI の設定 or 前回選択の記憶を検討
- **codex のサンドボックス配線（D25 の続き）**: パネルのサンドボックス設定は現状 claude のみに効く
  （`SpawnContext.SandboxEnabled` を CodexCli は無視）。codex は `--sandbox workspace-write` 等の
  独自フラグ体系を持つので、同じトグルにマップするなら書き込み許可ディレクトリ（~/.corhino）の
  指定方法と併せて別途設計が要る
- **`codex fork` / `archive` の UI 対応**: スコープ外とした操作。必要になったら Sessions Drawer に追加
- **exec モードの MCP auto-cancel（upstream #24135）**: CoRhino は TUI 利用なので現状無関係だが、
  headless 用途（将来の自動化）を作る場合はこのバグの解消が前提

## オンボーディング

- ~~claude CLI 未導入時の導線整理（旧 Phase 2b）~~ → D19 で実装済み（タブ内セットアップビュー。
  無言シェルフォールバックは廃止）
- **codex 未導入時にセットアップビューへ到達できない**: ＋メニューはグレーアウトするだけで
  クリック不可のため、codex を一度も入れていないマシンでは codex のセットアップビューを開く
  経路が UI に無い（出るのは「プローブ後に消えた」「未導入で resume」の場合だけ）。
  グレーアウト項目をクリック可能にし、セットアップビュー状態の codex タブを開くようにする
  （`StartSession` の missing 分岐がそのまま使える）
- **Windows のセットアップビュー実機検証**: B12 の修正（OS 別 allow-list / well-known 配置先）後、
  powershell + `irm` のインストーラ実行が PTY 環境で完走するかは未導入マシンでの実機確認待ち
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
- **タブ UX の作り込み**: 用途別プリセット（builder / reviewer 等）、ポート負荷の表示。
  タブのリネームは D10、タブ上限（MaxTabs=8）は D21 で実装済み。

## エージェントオーケストレーション（D21 の続き）

コマンドファイル・プロトコル（spawnTab / sendPrompt / closeTab + ready ゲート + マニフェスト +
`orchestrate-agents` スキル）は実装済み（2026-07-09、
→ [`plans/agent-orchestration-design.md`](plans/agent-orchestration-design.md) / D21）。
承認バナーは D22 で撤去（親の権限ゲートと二重・防御価値ゼロ。通知のみに変更）。残り:

- **実機確認（ゲート）**: フルサイクル（spawn→初期プロンプト配送→完了検知→transcript 回収→closeTab）、
  `not-your-child` スコープ、`tab-limit-reached`、孤児の継続動作、
  spawn 時のコマンドライン通知（緩和 permissionMode の警告表示）、SessionStart フック不発時の
  15s フォールバック。チェックリストは
  [`plans/agent-orchestration-tasks.md`](plans/agent-orchestration-tasks.md) Phase 8
- **V2 候補**: codex 子タブ（セッション ID 後発見の対応付け）/ dumpScreen（PTY リングバッファ）/
  クロスパネル操作 / オーケストレーションの opt-out・許可リスト設定（D22 で承認を撤去したため、
  明示統制が欲しくなった場合の再導入先はブロッキング承認ではなく設定）
- ~~PYTHONHOME 漏れの検証~~ → B19 で原因確定・修正済み（unix の Porta.Pty は継承+setenv で
  allow-list は隔離にならない → 毒変数スクラブ）。実機確認（セッション/子タブの `python3` と
  `run_python` の無影響）のみ残り
