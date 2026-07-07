# Codex CLI 対応 タスクリスト

| 項目 | 内容 |
|------|------|
| 対象 | タブ単位の Claude Code / Codex CLI 選択対応 |
| 設計書 | [`codex-cli-support-design.md`](codex-cli-support-design.md) |
| 作成日 | 2026-07-07 |
| テスト方針 | ユニットテスト基盤なし → ビルド（`dotnet build src/CoRhino.csproj`）+ `tsc` を各 Phase のゲートに。純粋ロジック（引数生成・rollout パース）は UI 非依存の static クラスに分離し将来のテスト導入に備える |
| レビュー方針 | 各 Phase 完了時に Codex レビュー → LGTM まで |
| 進捗管理 | 各 Phase 完了時にチェックボックスを [x] に更新 |

---

## Phase 0: スパイク（codex 実機検証・コード変更なし）✅ 完了（2026-07-07）

検証方法: 自作の最小 streamable-HTTP MCP サーバ（ping ツール 1 個、全リクエスト + ヘッダをログ）を
localhost:10590 に立て、`codex exec`（codex-cli 0.142.5）で各構成を実測。

- [x] **0-1**: `-c 'mcp_servers={...}'` 全置換の実効確認
  - **結果: 全置換ではなくマージ**。`codex mcp list -c 'mcp_servers={rhino=...}'` でユーザーの既存 6 サーバ + rhino が全部載る
  - **確定レシピ**（`codex mcp list` で rhino のみ enabled になることを実測）:
    `--disable plugins`（プラグイン注入分 computer-use / github を一括排除）
    + `-c mcp_servers.<実在名>.enabled=false`（config.toml の `[mcp_servers.*]` ごと）
    + `-c 'mcp_servers.rhino={url=...}'`
  - 注意: config.toml に**実在しない**サーバ名へ enabled=false を当てると設定エラー → spawn 時に config.toml のテーブル名を軽量パースする必要あり
  - HTTP 接続自体は成立: initialize → notifications/initialized → tools/list が POST で到達、GET は SSE チャネル（405 でも探索は動くが SSE 200 が正）
  - 補足: `--ignore-user-config`（config 完全無視 + auth 維持）は **exec 専用**で TUI に無い → 不採用
- [x] **0-2**: `http_headers` の対応確認
  - **結果: 対応**。`http_headers={"X-CoRhino-Agent"="agent-7"}` が initialize / initialized / tools/list の全リクエストに到達
- [x] **0-3**: レイヤ指示の注入経路確認
  - **結果: `-c developer_instructions="..."` が本命**（公式 config-reference に「既存指示への追記」と明記、実測で実効確認）。
    TUI に表示されず、AGENTS.md のルールとも併存する（同一セッションで両方の実効を確認）
  - 初回プロンプト引数方式は不要になった（設計書 5.3 を差し替え済み）
- [x] **0-4**: `codex resume <id>` の cwd 非依存性確認
  - **結果: 別 cwd（/tmp）から resume 成功**、会話コンテキストを保持。`-c mcp_servers` 併用も有効
    （resume 後に新ヘッダで initialize がサーバに到達）。**resume は同一 rollout ファイルへの追記**（claude の jsonl と同じ）
- [x] **0-5**: rollout 生成タイミングの計測
  - **結果: 起動数秒内に生成**（SessionConfigured イベント時点で rollout_path 確定、mtime はファイル名 ts + 数秒〜10s）。
    リトライ上限 15s は妥当。`session_meta` に `id` / `cwd` / `originator` / `source` を確認
  - AGENTS.md: workspace 直下のものを読むことを実測確認（マーカー語が全応答に反映）
  - 副産物: `session_index.jsonl` は exec セッションを即時反映しない → カタログの一次情報にしない（設計書 5.5 反映済み）
- [x] **0-7（追加発見）**: MCP ツール承認の仕様
  - `mcp_servers.<name>.tools.<tool>.approval_mode` / `default_tools_approval_mode` = `auto` | `prompt` | `approve`
  - **後日訂正（実機フィードバック）**: 当初 docs の記述から `auto`=プロンプトなしと解釈したが誤り。
    `auto` は「codex が判断」で run_python 等の危険ツールには TUI で承認が出る。
    **`approve` が「事前承認済み・プロンプトなし」** — approve なら exec モードでも tools/call が通ることを
    実測（= Phase 0 で「upstream バグ #24135 により exec は常に auto-cancel」とした結論も、承認設定が
    prompt 相当だったことが真因で、approve で回避可能だった）。spawn 引数は `approve` に修正済み

### 📋 Phase 0 完了処理
- [x] **0-6**: 検証結果を設計書 2 章 / 3 章 D14 / 4.3 / 5.3 / 5.5 / 6 章に反映済み。ユーザー報告済み

---

## Phase 1: アダプタ層導入（claude リファクタ・挙動変更なし）✅ コード完了（2026-07-07・実機スモークのみ残）

**このフェーズの完了条件は「claude の現行動作が 1 ミリも変わらないこと」**。codex はまだ登場しない。

- [x] **1-1**: `src/Cli/AgentCliKind.cs` + `src/Cli/IAgentCli.cs` + `SpawnContext` record を作成
  - 計画からの逸脱: `ScanSessions`/`DeleteSession` は interface に**含めなかった**（Phase 3 で使う面を
    先行定義すると Phase 1-2 の間 dead surface になる。Phase 3 で拡張する）。`LayerTokenFor` を追加
    （レイヤ戦略は CLI 差分なのでアダプタが所有）
- [x] **1-2**: `CliResolver.Resolve(string binaryName)` へ汎用化
  - 計画からの逸脱: **キャッシュは実装しない**（Codex レビュー指摘: 解決結果の固定は「起動中に CLI を
    インストールしたら次の spawn で拾える」現行挙動を壊す）。availability プローブは Phase 2 の
    `onCliAvailability` push（BG・パネル ready 時に一度）で賄う — 設計書 5.1 改訂済み
- [x] **1-3**: `src/Cli/ClaudeCli.cs` — `BuildArgs`（引数列は旧実装と同順） / `DiscoverSessionIdAsync`（即時返し）
- [x] **1-4**: `AgentTab.Cli` 追加、`StartSession` をアダプタ委譲に書き換え（PTY 配線・フォールバック・
  Windows ラップは Panel 残置）。`LayerFor` は `ClaudeCli.LayerTokenFor` へ移設
- [x] **1-5**: `src/Cli/AgentCliRegistry.cs`（Claude のみ登録。Codex は Phase 2 で追加）
- [x] **1-6**: ビルド ✅（0 警告 0 エラー）/ 実機スモーク ✅（2026-07-07 ユーザー確認: 問題なし）

### 📋 Codex レビュー
- [x] **1-7**: 実施済み（2 ラウンド）
  - R1 指摘 High: resolver キャッシュ = 挙動差分 → キャッシュ撤去で解消
  - R2: 挙動同一を確認（差は binaryName パラメータ化のみ）。引数列・スレッド規律とも問題なし
  - 申し送り: `CliResolver` の binaryName はシェル埋め込みのためアダプタ定数以外を渡さない（architecture.md 2.7 に明記）

---

## Phase 2: codex タブの起動（spawn + MCP + レイヤ + ID 発見）✅ コード完了（2026-07-07・実機スモークのみ残）

- [x] **2-1**: `Workspace.EnsureWorkspace()` に AGENTS.md seed を追加（D17）
  - テンプレを `WorkspaceGuideTemplate` に共通化（文言も CLI 中立に）。既存 workspace には AGENTS.md のみ後追い seed
- [x] **2-2**: `src/Panel/SessionLayers.cs` を作成（D16）
  - `~/.corhino/session-layers.json`、`SessionNames` と同型（lock + write-through、temp+move、剪定なし）
- [x] **2-3**: `src/Cli/CodexCli.cs` — `BuildArgs`（Phase 0 確定レシピ）
  - 実装どおり + 追加: `-c projects."<workspace>".trust_level="trusted"`（初回 trust プロンプト抑止・config.toml へ書かせない）
  - config.toml パーサ: bare / basic-quoted（\" と \\ のみ解決、他エスケープは skip+log）/ literal-quoted 対応。
    inline `mcp_servers = {...}` は検出して警告ログ（そのサーバは共存）。**不確実な名前は disable しない方向に倒す**
    （存在しない名前への enabled=false は起動を殺すため — R1 レビュー指摘の反映）
  - 生成引数列は実 codex 0.142.5 で `codex mcp list` 検証済み（rhino のみ enabled・trust/instructions 受理）
- [x] **2-4**: `CodexCli.DiscoverSessionIdAsync`（D15）
  - rollout 列挙（spawn 日 ±1 日）→ mtime フィルタ → 1 行目 `session_meta.cwd` 突合 → UUID 確定。500ms × 上限 15s
  - **設計強化（R2/R3 レビュー反映）**: claimed 集合だけでは帰属を証明できない（全タブ同一 cwd）→
    (a) process-wide `CodexSpawnGate`（SemaphoreSlim）で [起動 → claim] を直列化、
    (b) タブ除去時の cancel は **burn mode**（自 rollout を claim して捨てる。deadline は +4s に短縮）
- [x] **2-5**: `CoRhinoPanel` の spawn 経路統合
  - `SpawnTab(kind)` 化。codex タブは `SessionId=null` + `DiscoveryCts` 付きで開始。発見時に UI スレッドで
    `SessionId` 確定 + `LiveSessionIds` 登録 + `SessionLayers.Set` + `onTabSession` push（rename 有効化）
  - ゲートは StartSession 獲得 → discovery タスクへ引き渡し → finally で必ず解放（spawn 失敗時も）
- [x] **2-6**: UI — ＋ミニメニューとバッジ（fixed-position メニュー: #tabbar の overflow に食われないため。
  外側クリック / Esc で閉じる。availability 前の既定は claude=有効・codex=無効）
- [x] **2-7**: C# — `createTab {cli}` 受理、`onTabCreated {cli}` / `onCliAvailability`（ready 時 BG プローブ 1 回）/
  `onTabSession` push、codex タブの `listSkills`/`reloadSkills` ガード
- [x] **2-8**: UI — codex タブがアクティブな間 ⚡ を disabled（`SkillsDrawer.setEnabled`、ツールチップ切替）
- [x] **2-9**: ビルド ✅ + 生成引数の codex 実 CLI 検証 ✅ + 実機スモーク ✅（2026-07-07 ユーザー確認）
  - codex タブ作成 → Rhino-MCP ツールが `mcp__rhino` 名前空間で見える → `run_python` 等で実操作
    （**tools/call の E2E 最終ゲート**。Phase 0 では exec の upstream バグで TUI 未検証。
    `default_tools_approval_mode` でプロンプトなしに実行されることをここで確認 — 実機で auto では不十分と判明し approve に修正）
  - レイヤ指示（`developer_instructions`）が効いている（採番レイヤ配下に作図する）
  - claude タブとの並列動作、`~/.codex/config.toml` が無変更であること
  - タブ増減（codex 連続作成での直列化挙動含む）、codex タブの ×/exit → タブ撤去

### 📋 Codex レビュー
- [x] **2-10**: 実施済み（3 ラウンド）
  - R1 High: claimed 集合では帰属を証明できない（同時 spawn で ID 入れ替わり・除去済みタブの誤 claim）→ spawn ゲート直列化 + CTS cancel
  - R1 Medium: TOML パーサの穴（literal key・エスケープ・inline table）→ skip+log 方向に修正
  - R2 High: cancel が abort だと自 rollout が未 claim で残り次タブが誤 claim → **burn mode** に変更
  - R3: 全ポイント解消を確認（XSS・claude 挙動不変・ゲート解放経路とも問題なし）

---

## Phase 3: Sessions Drawer の codex 対応 ✅ コード完了（2026-07-07・実機スモークのみ残）

- [x] **3-1**: `src/Panel/CodexSessionsCatalog.cs` — `Scan()`
  - rollout 全走査 + 1 行目 `session_meta.cwd` フィルタ。head facts は path キー + mtime でキャッシュ
    （session_meta は不変、HasContent/FirstPrompt のみ mtime 変化で再読）
  - 表示名: 手動名（SessionNames、Panel 側）> `thread_name`（session_index.jsonl、遅延あり=ベストエフォート）
    > 先頭 `user_message` > id 短縮。空セッション除外（`user_message` 無し。chunk 超えファイルは内容ありとみなす）
  - allowlist（`TryGetKnown` / `TryGetKnownTitle`、claude 側と同型）
- [x] **3-2**: `RequestSessionsScan` で両カタログをマージ（`cli` フィールド付与、updated 降順の一体ソート）
- [x] **3-3**: resume の CLI 振り分け（カタログ横断で Kind 特定 → 該当アダプタで spawn。ID は UUID なので衝突しない）
  - transcript 存在再検証 + `IsSessionBlocked` ガード（下記）
- [x] **3-4**: delete の codex 経路（`CodexSessionsCatalog.Delete` = `codex delete --force <id>`、10s bound、
  失敗時は一覧維持 + ログ。索引・アーカイブの整合を CLI に任せるためファイル直接削除はしない）
  - 実機バグ修正: `--force` 無しだと非対話端末で確認プロンプトが出せず exit 1
    （"cannot confirm session deletion without an interactive terminal"）。ドロワーの 🗑 2 段階確認が
    同意ステップなので `--force` を付与。非対話条件（stdin 閉鎖）で削除成功を実地検証済み
- [x] **3-5**: rename は `SessionNames` 共用で codex にも効く。`ComputeLabel` を CLI 別カタログ振り分けに変更
  （live codex タブの自動タイトルも反映）
- [x] **3-6**: UI — `sessions-drawer.ts` の行頭に CLI バッジ（● / ◆、タブと同一スタイル）
- [x] **3-7**: ビルド ✅ + 実機スモーク ✅（2026-07-07 ユーザー確認。delete のみ `--force` 欠落で失敗 → 修正・検証済み）

### 📋 Codex レビュー
- [x] **3-8**: 実施済み（2 ラウンド）
  - R1 High: ID 未発見（または発見タイムアウト）の実行中 codex セッションが非 live 扱いになり
    二重 resume / live delete が可能 → **PendingCodexTabs** 集合 + 単一判定関数 `IsSessionBlocked`
    （LiveSessionIds ∪ DeletingSessionIds ∪ codex-pending-cutoff）を drawer 装飾と resume/delete ガードで共用
  - R1 Medium: `codex delete` 実行中（〜10s）の行が resume 可能 → **DeletingSessionIds 予約**（UI スレッド、
    完了時に解除 + 再スキャン。予約直後にも再スキャンして行を即ブロック表示）
  - R2: 両修正の解消を確認、LGTM

---

## Phase 3b: codex スキル対応（調査により追加。2026-07-07 実装）✅ コード完了（実機スモークのみ残）

前提調査（サブエージェント 2 系統: ローカル実機 0.142.5 / 公式 docs + ソース rust-v0.142.5）:
`<cwd>/.agents/skills/` と `<cwd>/.codex/skills/` の配置規約でセッション（ディレクトリ）限定注入が可能、
symlink 経由ロード実測 OK、SKILL.md は claude 互換、`--disable plugins` と干渉なし、in-session reload のみ
なし（詳細は設計書 5.8。当初「bundled は mount 不可」としたが `.codex/skills` ルートで解決 → 3b-7）。

- [x] **3b-1**: `Workspace.EnsureCodexSkillsLink` — `<workspace>/.agents/skills` → `~/.corhino/skills/skills`
  の symlink を無ければ seed（既存エントリはユーザー所有として不触。失敗は一度だけログ・claude 無影響）
- [x] **3b-2**: `IAgentCli` に `SupportsSkillsReload` / `LoadsBundledSkills` を追加
  （claude: true/true、codex: false/false。`SupportsSkillsDrawer` は codex も true に）
- [x] **3b-3**: C# — `reloadSkills` ガードを `SupportsSkillsReload` に変更、`SendSkillsList` が
  `LoadsBundledSkills=false` のタブへ bundled スキルを送らない（セッションに無いものは見せない）
- [x] **3b-4**: UI — ⚡ の disabled 機構を撤去（両 CLI 有効）。codex タブでは ⟳ 非表示
  （`getActiveCli` コールバック）、⚠ ヒント文言を「新しいタブから有効」に切替
- [x] **3b-7（追加）**: 同梱 cc-plugin スキルも codex へ mount
  - 実機フィードバック「save-rhino-skill がない」を受け追加。codex の第二プロジェクトルート
    `<cwd>/.codex/skills` に `<plugin dir>/cc-plugin/skills` への symlink を seed（`EnsureCodexSkillsLinks`）
  - capability を `LoadsBundledSkills` → `LoadsSkillsViaWorkspaceMount` に差し替え、ドロワーは
    ソースごとのリンク健全性（`Codex{User,Bundled}SkillsLinkActive`）で列挙を制御
  - SKILL.md 本文を CLI 中立化（reload 手順を claude 限定と明記）
  - E2E 実測: workspace cwd の `--ephemeral` exec で `corhino:save-rhino-skill` と
    `my-rhino-skills:*`（ユーザースキル）の両方がロードされることを確認（plugin 名前空間付き。
    codex は symlink 先の `.claude-plugin/plugin.json` も認識）。当初「出ない」ように見えたのは
    モデルの全列挙が名前空間付きスキルを省略していたためで、ロード自体は成功していた
- [ ] **3b-5**: 実機スモーク — codex タブで ⚡ が開き user + 同梱スキルが列挙される・スキルが実際に発動する・
  claude タブは従来どおり（⟳ あり）・`~/.codex` が無変更・symlink 2 本が workspace に生成される

### 📋 Codex レビュー
- [x] **3b-6**: 実施済み（2 ラウンド）
  - R1 指摘 1: symlink 不成立時（作成失敗・ユーザー差し替え）でも drawer が user スキルを列挙し
    「セッションに無いものを見せる」→ `Workspace.CodexSkillsLinkActive()`（LinkTarget がストアを
    指すことを検証）が true のときだけ列挙、false は空に
  - R1 指摘 2: 並列 spawn の check/create 競合で偽の失敗ログ → static lock で直列化 +
    create 例外時はエントリ再確認（現れていれば成功扱い）
  - R2: 両修正の解消を確認、LGTM

---

## Phase 4: ドキュメント追従 + 実機確認チェックリスト

- [ ] **4-1**: `docs/architecture.md` 更新（`Cli/` 章、プロトコル表、D14–D17 参照）
- [ ] **4-2**: `docs/design-history.md` に D14–D17 を記録（Phase 0 の検証ログ含む）
- [ ] **4-3**: `README.md` 更新（codex 要件・使い方）
- [ ] **4-4**: `docs/roadmap.md` 更新（codex Skills Drawer / codex ID 回転追跡 / Windows 検証ゲート / 初回タブ CLI 設定化）
- [ ] **4-5**: 総合実機確認
  - [ ] claude / codex 混在パネルで両タブが同一 doc を操作できる
  - [ ] codex タブのレイヤ分離（採番レイヤ配下のみ書き込み）
  - [ ] resume 後に同一レイヤが復元される
  - [ ] `~/.codex/config.toml` / `~/.claude` が無変更
  - [ ] codex 未導入マシン相当（PATH から外して）でメニューがグレーアウト、claude は通常動作
  - [ ] `/exit`（claude）・`Ctrl-D`（codex）での終了 → タブ撤去 → 最後のタブでパネルクローズ

- [x] **4-7（追加・実機フィードバック起点）**: codex タブを承認プロンプトなしで起動
  - `-a never -s workspace-write` を `CodexCli.BuildArgs` の既定に（`--full-auto` は 0.142.5 に無く
    `on-failure` は deprecated。`--dangerously-*` は不使用 — サンドボックス維持）。D15 系の
    design-history 追記・設計書 4.3 改訂済み

### 📋 Codex レビュー
- [x] **4-6**: 最終レビュー実施済み（ドキュメント整合中心。plans/ の stale 記述（スキル「スコープ外」「mount 不可」の残骸）・
  タスク消化ログの不整合・README の claude 固定表現 / `--force` 欠落・7 章 D5 トレース先の stale を検出 → 全修正。
  コード側の追加懸念なし）

---

## 実装順序

```
Phase 0（スパイク・設計確定）
  ↓ ⚠ 項目の結果次第で設計書 4.3 / 5.3 を修正
Phase 1（アダプタ層・claude 挙動不変）
  ↓ 1-6 スモーク pass が Phase 2 の前提
Phase 2（codex spawn + ID 発見 + UI）🟢→📋
  ↓ 2-9 スモーク pass が Phase 3 の前提
Phase 3（Sessions Drawer）🟢→📋
  ↓
Phase 4（ドキュメント + 総合実機確認）📋
```

- Phase 2 の UI タスク（2-6〜2-8）は 2-1〜2-5 と並行可能（プロトコルは設計書 5.7 で確定済み）
- Phase 3 は Phase 2 の ID 発見（2-4）に依存（実行中装飾・resume ガードが SessionId 前提）
