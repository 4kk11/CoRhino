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
  - `mcp_servers.<name>.tools.<tool>.approval_mode` / `default_tools_approval_mode` = `auto` | `prompt` | `approve`。
    `auto` = TUI でプロンプトなし実行（公式 docs）→ spawn 引数に `default_tools_approval_mode="auto"` を追加（設計書 4.3）
  - **exec（非対話）モードは upstream バグで MCP ツール呼び出しが常に auto-cancel**
    （debug ログで `ResolveElicitation decision: Cancel` を確認。openai/codex #24135 / #16685）。
    CoRhino は TUI 利用なので影響なしだが、**tools/call の E2E は Phase 2 の Rhino 実機スモークが最終ゲート**（2-9 に明記）

### 📋 Phase 0 完了処理
- [x] **0-6**: 検証結果を設計書 2 章 / 3 章 D13 / 4.3 / 5.3 / 5.5 / 6 章に反映済み。ユーザー報告済み

---

## Phase 1: アダプタ層導入（claude リファクタ・挙動変更なし）

**このフェーズの完了条件は「claude の現行動作が 1 ミリも変わらないこと」**。codex はまだ登場しない。

- [ ] **1-1**: `src/Cli/AgentCliKind.cs` + `src/Cli/IAgentCli.cs` + `SpawnContext` record を作成
  - 設計書 4.2 のシグネチャ。`SessionEntry` は既存 SessionsCatalog の型を流用または昇格
- [ ] **1-2**: `CliResolver.Resolve(string binaryName)` へ汎用化 + 結果キャッシュ
  - `claude` ハードコードを排除。既存呼び出し元は `Resolve("claude")` に
  - キャッシュは binary 名キー・プロセス生存中有効（PATH 変更は Rhino 再起動で拾う扱い）
- [ ] **1-3**: `src/Cli/ClaudeCli.cs` を作成し、`StartSession` の claude 固有部を移設
  - `BuildArgs`: `--strict-mcp-config` / `--mcp-config` / `--plugin-dir` ×2 / `--session-id` | `--resume` / `--append-system-prompt` の組み立て
  - `DiscoverSessionIdAsync`: `ctx.SessionId` を即時返す（claude は事前採番）
  - `ScanSessions` / `DeleteSession`: 既存 `SessionsCatalog` への委譲
- [ ] **1-4**: `AgentTab` に `Cli`（`IAgentCli`）を追加し、`StartSession` をアダプタ委譲に書き換え
  - Windows の `.exe` 判定 / `cmd.exe /c` ラップ、PTY spawn、フォールバックは Panel 側に残す（CLI 非依存の PTY 配線）
  - `LayerFor` の呼び出し点を `SpawnContext.LayerToken` の組み立てに一元化
- [ ] **1-5**: `src/Cli/AgentCliRegistry.cs`（Kind→instance、当面 Claude のみ登録）
- [ ] **1-6**: ビルド + 実機スモーク（パネル起動 → agent-1 が従来どおり claude で立ち、MCP 接続・タブ増減・resume が動く）

### 📋 Codex レビュー
- [ ] **1-7**: 対象: `src/Cli/*`, `CoRhinoPanel.cs`, `CliResolver.cs`
  - 観点: 挙動不変の確認（引数列の一致）、スレッド規律（UI スレッド変異ルール）の維持

---

## Phase 2: codex タブの起動（spawn + MCP + レイヤ + ID 発見）

- [ ] **2-1**: `Workspace.EnsureWorkspace()` に AGENTS.md seed を追加（D16）
  - テンプレ本文を CLAUDE.md と共通化（定数を 1 か所に）。既存 workspace には AGENTS.md のみ後追い seed
- [ ] **2-2**: `src/Panel/SessionLayers.cs` を作成（D15）
  - `~/.corhino/session-layers.json`、`SessionNames` と同型（lock + write-through、剪定なし）
- [ ] **2-3**: `src/Cli/CodexCli.cs` — `BuildArgs`（Phase 0 確定レシピ）
  - `--disable plugins` + 既存サーバの `-c mcp_servers.<name>.enabled=false` 群 + rhino 追加（`http_headers` + `default_tools_approval_mode="auto"`）
  - `~/.codex/config.toml` の `[mcp_servers.<name>]` テーブルヘッダの軽量パーサ（読み取りのみ・UI 非依存の static・パース失敗時は disable 群なしで spawn + `Log.Write`）
  - レイヤ指示は `-c developer_instructions="..."`（L10n 経由）。TOML inline table / 文字列のエスケープは純粋関数に分離
  - 新規: レイヤトークン採番（`s-<8hex>`）/ resume: `codex resume <id>` + `SessionLayers` 復元（`-c` 群は resume でも毎回付ける）
- [ ] **2-4**: `CodexCli.DiscoverSessionIdAsync`（D14）
  - rollout 列挙（spawn 日 + 翌日 UTC）→ ファイル名 timestamp フィルタ → 1 行目 `session_meta.cwd` 突合 → UUID 確定
  - 500ms × 上限 15s リトライ、claimed 集合による並列 spawn 競合回避、失敗時 null + `Log.Write`
  - rollout 1 行目パーサは UI 非依存の static メソッドに分離
- [ ] **2-5**: `CoRhinoPanel` の spawn 経路統合
  - `SpawnTab(kind)` 化。codex タブは `SessionId=null` で開始、`DiscoverSessionIdAsync` 完了時に UI スレッドで `SessionId` 確定 + `LiveSessionIds` 登録 + `SessionLayers.Set`
  - `RemoveTab` / `EnsureSessionAlive` / `ReleaseSessionOnLaunchExit` の SessionId null 経路がタブ Kind に関わらず安全なことを確認
- [ ] **2-6**: UI — ＋ミニメニューとバッジ
  - `main.ts`: ＋クリックでメニュー（外側クリック / Esc で閉じる、ドロワーと排他）、`createTab {cli}` 送信
  - タブラベルに CLI バッジ（● / ◆）。`onTabCreated` の `cli` フィールド対応
  - `onCliAvailability` 受信 → 未導入 CLI をグレーアウト + ツールチップ
- [ ] **2-7**: C# — `createTab {cli}` 受理、`onTabCreated {cli}` / `onCliAvailability` push、codex タブの `listSkills`/`reloadSkills` no-op ガード
- [ ] **2-8**: UI — codex タブがアクティブな間 ⚡ を disabled 表示
- [ ] **2-9**: ビルド + tsc + 実機スモーク
  - codex タブ作成 → Rhino-MCP ツールが `mcp__rhino` 名前空間で見える → `run_python` 等で実操作
    （**tools/call の E2E 最終ゲート**。Phase 0 では exec の upstream バグで TUI 未検証。
    `default_tools_approval_mode="auto"` でプロンプトなしに実行されることをここで確認）
  - レイヤ指示（`developer_instructions`）が効いている（採番レイヤ配下に作図する）
  - claude タブとの並列動作、`~/.codex/config.toml` が無変更であること

### 📋 Codex レビュー
- [ ] **2-10**: 対象: `CodexCli.cs`, `SessionLayers.cs`, `Workspace.cs`, `CoRhinoPanel.cs`, `main.ts`
  - 観点: ID 発見のレース（claimed 集合・UI スレッド変異）、TOML エスケープ、null SessionId 経路、XSS（バッジ/メニューは定数だが規約準拠）

---

## Phase 3: Sessions Drawer の codex 対応

- [ ] **3-1**: `src/Panel/CodexSessionsCatalog.cs` — `Scan()`
  - `session_index.jsonl` 索引化 + rollout 1 行目走査（cwd フィルタ、`(path, mtime)` キャッシュ）
  - 表示名の優先順位: 手動名 > `thread_name` > 先頭プロンプト > id 短縮。空セッション除外
  - allowlist（`TryGetKnown` 同型）
- [ ] **3-2**: `SendSessionsList` のマージ（claude + codex、`cli` フィールド付与、updated 降順の一体ソート）
  - BG スキャンの single-flight 規律は既存 `RequestSessionsScan` に相乗り
- [ ] **3-3**: resume の CLI 振り分け
  - `resumeSession {sessionId}` → カタログ横断で Kind 特定 → 該当アダプタで新規タブ spawn
  - spawn 前の rollout / jsonl 存在再検証（消えた ID による即死ガード、既存と同型）
- [ ] **3-4**: delete の codex 経路（`codex delete <id>`、timeout 10s、失敗時は一覧維持 + ログ）
- [ ] **3-5**: rename が codex セッションでも効くこと（`SessionNames` 共用）＋ live codex タブへの `onTabLabel` 反映
- [ ] **3-6**: UI — `sessions-drawer.ts` に CLI バッジ列を追加
- [ ] **3-7**: ビルド + tsc + 実機スモーク（codex セッションの一覧表示・resume でレイヤ復元・rename・delete・実行中グレーアウト）

### 📋 Codex レビュー
- [ ] **3-8**: 対象: `CodexSessionsCatalog.cs`, `CoRhinoPanel.cs`, `sessions-drawer.ts`
  - 観点: BG スレッド規律、allowlist の抜け（任意 ID resume/delete 防止）、`codex delete` の失敗処理

---

## Phase 4: ドキュメント追従 + 実機確認チェックリスト

- [ ] **4-1**: `docs/architecture.md` 更新（`Cli/` 章、プロトコル表、D12–D16 参照）
- [ ] **4-2**: `docs/design-history.md` に D12–D16 を記録（Phase 0 の検証ログ含む）
- [ ] **4-3**: `README.md` 更新（codex 要件・使い方）
- [ ] **4-4**: `docs/roadmap.md` 更新（codex Skills Drawer / codex ID 回転追跡 / Windows 検証ゲート / 初回タブ CLI 設定化）
- [ ] **4-5**: 総合実機確認
  - [ ] claude / codex 混在パネルで両タブが同一 doc を操作できる
  - [ ] codex タブのレイヤ分離（採番レイヤ配下のみ書き込み）
  - [ ] resume 後に同一レイヤが復元される
  - [ ] `~/.codex/config.toml` / `~/.claude` が無変更
  - [ ] codex 未導入マシン相当（PATH から外して）でメニューがグレーアウト、claude は通常動作
  - [ ] `/exit`（claude）・`Ctrl-D`（codex）での終了 → タブ撤去 → 最後のタブでパネルクローズ

### 📋 Codex レビュー
- [ ] **4-6**: 最終レビュー（差分全体、ドキュメント整合含む）

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
