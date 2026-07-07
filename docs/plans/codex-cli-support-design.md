# Codex CLI 対応 設計書

| 項目 | 内容 |
|------|------|
| 対象 | タブ単位で Claude Code / Codex CLI を選択可能にする |
| 作成日 | 2026-07-07 |
| ステータス | Draft |
| 検証環境 | codex-cli 0.142.5 / claude CLI（現行） |

---

## 1. 概要

### 1.1 目的

CoRhino のタブ（＝エージェント）は現在 `claude` CLI 専用。これを **タブ単位で claude / codex を選択可能**にし、同一パネル内で両 CLI のエージェントを並列に走らせられるようにする。

**意図**: CoRhino の価値は「パネルを開くだけで、いま開いている Rhino を AI エージェントに操作させられる」配線にある。エージェント本体（CLI）は交換可能な部品であるべきで、claude 固有の配線がオーケストレータに直書きされている現状は D2（責務境界）の精神に反する。Codex 対応はその最初の実証でもある。

### 1.2 ユースケース

- codex を新規タブで起動し、Rhino-MCP 経由で「いま開いている Rhino」を操作させる
- 同一 doc 上で claude タブと codex タブを並列に走らせ、出力を比較・分担させる
- codex の過去セッションを Sessions Drawer から resume / rename / delete する

### 1.3 決定済み仕様（インタビュー結果）

| 論点 | 決定 |
|---|---|
| 選択単位 | **タブごと**（1 パネル内に claude / codex 混在可） |
| スコープ | **段階的フル対応**: Phase 1-2 = 起動 + MCP 接続 + レイヤ分離、Phase 3 = Sessions Drawer。Skills Drawer は codex の skills 機構検証後に判断（今回スコープ外 → roadmap） |
| 設定分離 | **`-c` オーバーライドで `mcp_servers` テーブルを全置換**。`~/.codex/config.toml`・auth.json は無変更（再ログイン不要） |
| 抽象化 | **CLI アダプタ層（`IAgentCli`）を導入**。claude 固有ロジックを `CoRhinoPanel` から追い出す |
| タブ作成 UI | **＋クリックでミニメニュー**（Claude Code / Codex）。未導入 CLI はグレーアウト。タブに CLI 種別バッジ |
| 初回タブ | **常に claude**（現行動作維持。オンボーディングが claude 前提で成熟しているため） |

---

## 2. 前提: 両 CLI の機能差分（Phase 0 スパイク検証済み）

codex-cli 0.142.5 のローカル実機（自作 streamable-HTTP MCP サーバ + `codex exec`）で 2026-07-07 に検証済み。
検証ログの詳細は [`codex-cli-support-tasks.md`](codex-cli-support-tasks.md) Phase 0 の各タスク注記を参照。

| 観点 | Claude Code | Codex CLI | 状態 |
|---|---|---|---|
| MCP 隔離注入 | `--strict-mcp-config --mcp-config <file>` | **`-c 'mcp_servers={...}' は全置換ではなくマージ**。隔離は「既存サーバ個別 disable + plugins 一括 disable + rhino 追加」の合成で実現（→ 4.3） | ✅ 実測 |
| HTTP MCP | `"type": "http"` | streamable HTTP 正式サポート。initialize→initialized→tools/list の POST + GET SSE チャネルを確認 | ✅ 実測 |
| HTTP ヘッダ | `headers` フィールド | `http_headers`。`X-CoRhino-Agent` の全リクエスト到達を確認 | ✅ 実測 |
| MCP ツール承認 | claude 側の permission 機構 | `default_tools_approval_mode` / `tools.<name>.approval_mode`（`auto`=プロンプトなし実行 / `prompt` / `approve`）。**exec モードは upstream バグで常に auto-cancel**（[#24135](https://github.com/openai/codex/issues/24135)）だが TUI は `auto` で動く（公式 docs 確認。tools/call の E2E は Phase 2 実機ゲート） | ✅ 設定確認 / ⚠ TUI 実機ゲート |
| セッション ID 固定 | `--session-id <GUID>`（spawn 時に C# 採番） | **不可**（codex が生成） | ✅ |
| resume | `--resume <id>`（cwd 一致必須） | `codex resume <UUID>`。**別 cwd から動作・`-c` オーバーライド併用可・同一 rollout に追記** | ✅ 実測 |
| トランスクリプト | `~/.claude/projects/<cwd-slug>/*.jsonl` | `~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl`。1 行目 `session_meta` に `id` / `cwd` / `originator` / `source`。**起動数秒内に生成** | ✅ 実測 |
| セッション索引 | なし（jsonl を走査） | `~/.codex/session_index.jsonl`（`id` / `thread_name` / `updated_at`。**cwd なし・exec セッションは即時反映されない**） | ✅ 実測（補助情報扱い） |
| 自動タイトル | jsonl 内 `ai-title` | `session_index.jsonl` の `thread_name`（ベストエフォート） | ✅ |
| システムプロンプト注入 | `--append-system-prompt` | **`-c developer_instructions="..."`**（既存指示に追記・TUI に不可視・実効を実測確認） | ✅ 実測 |
| ワークスペース指示 | `CLAUDE.md` | `AGENTS.md`（cwd から自動ロード、実効を実測確認） | ✅ 実測 |
| セッション削除 | ファイル直接削除 | `codex delete --force <id>`（索引も掃除される。`--force` 必須: 非対話端末では確認プロンプトを出せず exit 1 — 実機で確認） | ✅ 実測 |
| スキル/プラグイン | `--plugin-dir`（隔離ロード） | `codex plugin` / `~/.codex/skills`（隔離注入手段は未確認） | スコープ外 |

---

## 3. 設計判断（なぜこの形か）

- **D12: CLI アダプタ層 `IAgentCli`** — spawn 引数・セッション同定・カタログ・レイヤ注入の CLI 差分をアダプタに閉じ込める。`CoRhinoPanel` は「タブに紐付いたアダプタを呼ぶ」だけになり、3 つ目の CLI 追加時にオーケストレータを触らない。pre-alpha の破壊的変更 OK 方針なので、claude 経路も同時にアダプタへ移す（互換シムを作らない）。
- **D13: codex の隔離は「個別 disable + plugins 一括 disable + rhino 追加」の `-c` 合成** — `CODEX_HOME` 分離は auth まで分離され再ログインが必要になり、「パネルを開くだけ」のオンボーディング目標に反する。当初案の `-c 'mcp_servers={...}'` 全置換は**スパイクでマージ挙動と判明**したため、(1) `~/.codex/config.toml` の `[mcp_servers.*]` 実在エントリを `enabled=false` で個別無効化、(2) プラグイン注入分（computer-use / github 等、config.toml に現れない）は `--disable plugins` で一括排除、(3) rhino を追加、の合成で同じ結果（**rhino のみ enabled**）を得る。実測で確認済み。副作用: CoRhino が spawn 時にユーザーの config.toml から `[mcp_servers.<name>]` テーブル名を読む（読み取りのみ・軽量パース）。実在しない名前に `enabled=false` を当てると設定エラーになるため、実在名だけを対象にする。`--ignore-user-config`（完全無視 + auth 維持）は exec 専用で TUI に無く不採用。
- **D14: codex のセッション ID は事後発見** — codex は ID 固定不可。spawn 時刻を記録し、`~/.codex/sessions/` の新規 rollout（ファイル名に UUID、1 行目 `session_meta.cwd` == 共有 workspace）を突合して確定する。確定までタブの `SessionId` は null（= claude の shell フォールバックと同じ既存の扱い。resume ガード `LiveSessionIds` への登録も確定時）。
- **D15: codex のレイヤは C# 採番トークン + 永続マッピング** — claude はレイヤを `LayerFor(sessionId)` で決定的導出するが、codex は spawn 時に ID が無いので導出できない。spawn 時に C# がトークン（`s-<8hex>`）を採番して注入し、ID 確定後に `~/.corhino/session-layers.json`（sessionId→layer）へ永続化する。resume 時はこのマッピングから同じレイヤを復元する。
- **D16: codex は AGENTS.md、claude は CLAUDE.md を同一テンプレから seed** — 共有 workspace の並列エージェントルールは CLI に依存しない。テンプレを共通化し、`EnsureWorkspace` が両ファイルを「無ければ seed」（以後ユーザー所有、既存方針どおり）。

---

## 4. アダプタ層の設計

### 4.1 型と配置

```
src/
├─ Cli/                              ← 新設。CLI 差分はすべてここに閉じる
│  ├─ AgentCliKind.cs                enum { Claude, Codex }
│  ├─ IAgentCli.cs                   アダプタ interface
│  ├─ ClaudeCli.cs                   現行 StartSession の claude 固有部を移設
│  ├─ CodexCli.cs                    codex 実装
│  └─ AgentCliRegistry.cs            Kind → シングルトン instance の解決
├─ Pty/
│  └─ CliResolver.cs                 ★ 汎用化: Resolve(string binaryName)
└─ Panel/
   ├─ CoRhinoPanel.cs                ★ AgentTab に Cli 追加、StartSession をアダプタ委譲に
   ├─ Workspace.cs                   ★ AGENTS.md seed 追加
   ├─ SessionsCatalog.cs             claude カタログ（既存、rename のみ検討）
   ├─ CodexSessionsCatalog.cs        codex カタログ（新設、Phase 3）
   └─ SessionLayers.cs               sessionId→layer 永続化（新設、SessionNames と同型）
```

### 4.2 `IAgentCli` interface

```csharp
internal interface IAgentCli
{
    AgentCliKind Kind { get; }
    string DisplayName { get; }        // "Claude Code" / "Codex"
    string BinaryName { get; }         // "claude" / "codex"

    // このセッションの担当レイヤ。claude=sessionId から決定的導出（旧 LayerFor）、
    // codex=SessionLayers の復元 or 新規採番（Phase 2）。null in → null out。
    string? LayerTokenFor(string? sessionId);

    // spawn 引数を組み立てる。sessionId は claude=事前採番 GUID / codex=null。
    // resume 時は sessionId 必須（両 CLI とも既知 ID を渡す）。
    // 必要なファイル（claude の .mcp.json 等）はここで準備してよい。
    IReadOnlyList<string> BuildArgs(SpawnContext ctx);

    // spawn 後のセッション ID 解決。claude=即時（ctx.SessionId をそのまま返す）、
    // codex=rollout 突合（BG スレッドでポーリング、見つからなければ null のまま）。
    Task<string?> DiscoverSessionIdAsync(SpawnContext ctx, CancellationToken ct);

    bool SupportsSkillsDrawer { get; }  // claude=true / codex=false（当面）
}

internal sealed record SpawnContext(
    string AgentId, int HostPort, string Cwd,
    string? SessionId, bool Resume, string? LayerToken,
    DateTime SpawnedAtUtc);
```

**選択理由**: interface のメソッドは「CoRhinoPanel が現に CLI 差分で分岐する箇所」だけに限定する（PTY 入出力・タブ管理・WebView 中継は CLI 非依存なので含めない）。`SpawnContext` を record で渡すのは、引数リストの増殖と「アダプタが Panel の内部状態に触る」結合を防ぐため。
Sessions Drawer 用のカタログ面（`ScanSessions` / `DeleteSession`）は当初ここに置く案だったが、
Phase 1-2 の間 dead surface になるため **Phase 3 で拡張**する（実装済み contract が正）。

### 4.3 各アダプタの引数生成

**ClaudeCli（現行ロジックの移設。挙動変更なし）**:

```
claude --strict-mcp-config --mcp-config <runtime/mcp-<port>-<agentId>.json>
       --plugin-dir <~/.corhino/skills> [--plugin-dir <bundled cc-plugin>]
       (--session-id <GUID> | --resume <id>)
       --append-system-prompt "担当レイヤは `<layerToken>` …"
```

**CodexCli（新規・スパイク確定形）**:

```
# 新規:
codex --disable plugins
      -c 'mcp_servers.<name>.enabled=false'          # ユーザー config.toml の実在エントリごとに繰り返し
      -c 'mcp_servers.rhino={url="http://localhost:<port>/",http_headers={"X-CoRhino-Agent"="<agentId>"},default_tools_approval_mode="auto"}'
      -c 'projects."<workspace>".trust_level="trusted"'   # 初回 trust プロンプト抑止（config.toml へ書かせない）
      -c 'developer_instructions="<レイヤ指示>"'

# resume（同じ -c 群を毎回付ける。設定はプロセス限りで rollout に残らないため）:
codex resume <sessionId> --disable plugins -c ... （同上）
```

- `-c` の値は TOML inline table としてパースされる。`mcp_servers` はマージ挙動のため、既存サーバの排除は D13 の合成レシピで行う
- `<name>` の列挙は spawn 時に `~/.codex/config.toml` の `[mcp_servers.<name>]` テーブルヘッダを軽量パース（読み取りのみ）。パース失敗時は disable 群を諦めて rhino 追加だけで spawn し、`Log.Write` に記録（起動不能より共存を選ぶ）
- `default_tools_approval_mode="auto"` で rhino ツールは TUI で承認プロンプトなしに実行される（公式 docs 確認済み。E2E は Phase 2 実機ゲート）
- `developer_instructions` は既存指示への**追記**で TUI に表示されない（実測確認）。claude の `--append-system-prompt` と等価に使う
- codex は `.mcp.json` 相当のファイルを必要としない → `Workspace.EnsureAgentMcpConfig` は claude 専用のまま
- サンドボックス/承認ポリシーは codex の既定に任せる（TUI で対話するのはユーザー自身。CoRhino が `--dangerously-*` を注入することは**しない**）

---

## 5. 各機構の設計

### 5.1 CLI 解決（`CliResolver`）

現行の `Resolve()` は `claude` ハードコード。`Resolve(string binaryName)` に汎用化する。
**キャッシュはしない**（spawn ごとに毎回解決 = 現行挙動維持。キャッシュすると Rhino 起動中にインストールされた
CLI を拾えなくなる — Phase 1 Codex レビュー指摘で当初案から変更）。＋メニューの availability は
パネル ready 時に BG スレッドで一度プローブして `onCliAvailability` を push する（メニュー描画は同期・
プローブ結果は panel が保持。スタート後にインストールされた CLI は次のパネル or 明示リフレッシュで反映）。

- Unix: `bash -lc "command -v <bin> || which <bin>"` （現行と同じ。binaryName はアダプタ定数のみ渡す）
- Windows: `where.exe <bin>` + `.cmd` shim 解決（現行と同じ。codex も npm 配布なので shim 対策がそのまま効く）
- codex が見つからない場合: ＋メニューの Codex 項目をグレーアウト（→ 5.6）。**shell フォールバックはしない**（明示選択制なので、無いものは選ばせないのが正しい）

### 5.2 codex セッション ID の事後発見（D14）

```
spawn 直前: SpawnedAtUtc を記録
spawn 後 BG タスク（DiscoverSessionIdAsync）:
  1. ~/.codex/sessions/<spawn日 ±1 日>/ の rollout-*.jsonl を列挙（local/UTC 日付の曖昧さと日跨ぎに免疫）
  2. mtime >= SpawnedAtUtc - 5s のものを候補に
  3. 各候補の 1 行目 session_meta を読み cwd == ~/.corhino/workspace を確認
  4. 一致 → ファイル名から UUID を取得し claim（claimed 集合へ）→ 確定
  5. 未発見なら 500ms 間隔でリトライ（上限 ~15s。TUI 起動が遅い環境を考慮）
確定時（UI スレッドへ AsyncInvoke、タブ生存を ReferenceEquals で確認）:
  - tab.SessionId = id / LiveSessionIds へ登録 / onTabSession を UI へ push（rename に必要）
  - SessionLayers.Set(id, layerToken) を永続化（D15。タブ消滅後でも永続化はする — その rollout の実態）
```

- **並列 spawn の競合（帰属保証）**: 全 codex タブが同一 cwd を共有するため、「新しい rollout + cwd 一致」だけでは
  同時 in-flight の 2 spawn を区別できない（claimed 集合は再 claim を防ぐだけで帰属を証明しない — Phase 2
  Codex レビュー指摘）。**process-wide の spawn ゲート（SemaphoreSlim）で [codex 起動 → rollout claim] を直列化**し、
  未 claim の一致 rollout が常に高々 1 つの状態を保証する。ゲートは StartSession が獲得し discovery タスクへ
  引き渡し、finally で必ず解放。2 個目の codex タブは前のタブの ID 確定（通常数秒）を待ってから起動する
- **タブ除去との競合（burn mode）**: 除去済みタブの discovery を単純中断すると、そのタブの rollout が未 claim の
  まま残り次タブが誤 claim し得る。cancel は中断ではなく **burn mode** — ゲート保持中に自分の rollout を
  claim だけして帰属せず捨てる（deadline は now+4s に短縮: rollout を書く前に死んだプロセスはもう書かない）
- **発見失敗時**: `SessionId` は null のまま動作継続（ターミナルとしては完全に機能する）。resume 対象にならない・レイヤマッピングが残らないだけ。`Log.Write` に記録
- 走査は必ず BG スレッド（SessionsCatalog と同じ規律）。読むのは 1 行目のみで軽量

### 5.3 codex へのレイヤ指示注入（D15）

**`-c developer_instructions="<指示>"`** で注入する（スパイクで確定）。既存指示への**追記**であり、
TUI には表示されず、AGENTS.md とも併存する（両方の実効を同一セッションで実測確認済み）。
当初案の「初回プロンプト引数」（TUI に見える・1 ターン消費）は不要になった。

- 新規タブ: `s-<8hex>` を C# が乱数採番（claude の `LayerFor` と同じ見た目に揃える）
- resume タブ: `SessionLayers.TryGet(sessionId)` で復元。マッピングが無い場合（発見失敗した過去セッション等）は新規採番して上書き保存
- 指示文は claude の `--append-system-prompt` と同文（L10n 経由、spawn 時の Rhino 言語）

`SessionLayers.cs` は `SessionNames.cs` と同型（`~/.corhino/session-layers.json`、lock + write-through、V1 では剪定しない）。

### 5.4 共有ワークスペース（D16）

- `Workspace.EnsureWorkspace()` が `CLAUDE.md` に加えて `AGENTS.md` も「無ければ seed」する
- テンプレ本文は共通化する（現行 CLAUDE.md テンプレの内容は CLI 非依存: 並列エージェントルール・RhinoMCP ツール・進め方）。symlink ではなく**別ファイルとして seed**（ユーザーが CLI ごとに育て分けられる余地を残す。既存の「seed 後はユーザー所有」方針とも整合）
- 既存ユーザーの workspace には CLAUDE.md しか無い → AGENTS.md だけ後から seed される（`File.Exists` ガードが自然にそう動く）

### 5.5 Sessions Drawer の codex 対応（Phase 3）

```
SendSessionsList:
  claude 分 = SessionsCatalog.Scan()          （既存）
  codex 分  = CodexSessionsCatalog.Scan()     （新設）
  → SessionEntry に cli フィールドを追加してマージ、updated 降順で一体ソート
```

**CodexSessionsCatalog.Scan()（BG スレッド）**:

1. `session_index.jsonl` を読み `id → (thread_name, updated_at)` を索引化（cwd が無いのでこれだけでは列挙できない。
   さらに**索引への反映は遅延・欠落し得る**（exec セッションが載らないことをスパイクで確認）ため、あくまで表示名の補助）
2. `~/.codex/sessions/**/rollout-*.jsonl` を列挙し、**1 行目のみ**読んで `cwd == workspace` のものに絞る（他プロジェクトの codex セッションを混ぜない）。1 行目の `session_meta` 読み取り結果は `(path, mtime) → meta` でキャッシュし、再スキャンの I/O を抑える
3. 表示名の優先順位: 手動名（`SessionNames`、claude と共通のストア） > `thread_name` > 先頭ユーザープロンプト（rollout から取得できれば） > id 短縮
4. 空セッション除外・実行中装飾は claude と同じ規律（装飾は UI スレッドの `SendSessionsList` が付与）

**ブロック判定の一元化（Phase 3 レビューで追加）**: 「触ってはいけないセッション」の定義は単一関数
`IsSessionBlocked(sessionId, cli, lastActivityUtc)` に集約し、drawer 装飾（isLive）と resume / delete
ガードの三箇所すべてが同じ判定を通る。ブロック条件は:

- `LiveSessionIds`（タブに紐付いた実行中セッション）
- `DeletingSessionIds`（`codex delete` 実行中の予約。UI スレッドで delete 開始前に登録・完了時に解除。
  最大 ~10s の BG 実行中に同じ行を resume できてしまう race を塞ぐ）
- **codex の pending カットオフ**: ID 未発見（発見タイムアウト含む）の実行中 codex タブが存在する間、
  その最古タブの作成時刻 −5s 以降に更新された codex セッションはすべてブロック。ID が無い実行中
  セッションは `LiveSessionIds` に入れられず、rollout はディスクに実在して一覧に載ってしまうため
  （証明できないものは触らせない、の保守側に倒す。発見成功で即解除される）

**操作**:

| 操作 | claude（既存） | codex |
|---|---|---|
| resume | `--resume <id>` で新規タブ spawn | `codex resume <id>` で新規タブ spawn（タブの Kind=Codex） |
| rename | `SessionNames` | 同じ `SessionNames`（id は UUID なので衝突しない） |
| delete | jsonl + subagents dir 削除 | `codex delete --force <id>` を Process 実行（`--force` 必須: 非対話端末では確認プロンプトを出せず exit 1。索引・アーカイブの整合を CLI に任せる） |
| allowlist | `TryGetKnown`（直近 Scan の id のみ） | 同型の allowlist を CodexSessionsCatalog に持つ |

### 5.6 UI（タブバー・メニュー・バッジ）

```
タブバー:
┌────────────────────────────────────────────┐
│ [● agent-1 ×] [◆ agent-2 ×] [＋]     🕘 ⚡ │
└──────────────────────┬─────────────────────┘
                       ＋クリック↓
                ┌────────────────┐
                │ ● Claude Code  │
                │ ◆ Codex        │   ← 未導入はグレーアウト + "not found on PATH"
                └────────────────┘
```

- `＋` クリックでミニメニュー（ドロワーと同じ理由でネイティブ context menu は使わず DOM で自作）。外側クリック / Esc で閉じる。ドロワー開閉と同じく開いたら他ポップアップは閉じる
- タブラベル先頭に CLI バッジ（● claude / ◆ codex。色分けは xterm テーマに合わせる）。Sessions Drawer の行にも同じバッジ
- バッジ・メニュー項目名は定数文字列。ユーザー由来テキストではないので innerHTML 可だが、既存規約に合わせ `textContent` で統一
- CLI availability は C# が `onCliAvailability({claude: bool, codex: bool})` で ready 時に push（メニュー描画時に問い合わせない。解決結果はキャッシュ済み）

### 5.7 メッセージプロトコル変更

| メッセージ | 変更 |
|---|---|
| `createTab` | `{cli: "claude" \| "codex"}` を追加（省略時 claude） |
| `onTabCreated` | `{agentId, sessionId, label, cli}` に拡張 |
| `onSessionsList` | 各 entry に `cli` を追加 |
| `resumeSession` | 変更なし（sessionId から C# 側がカタログ横断で CLI を特定） |
| `onCliAvailability` | 新設（C#→UI、ready 応答時に push） |
| `listSkills` / `reloadSkills` | codex タブがアクティブな間は ⚡ ボタンを disabled（UI 側制御）。C# 側も Kind=Codex なら no-op ガード |

### 5.8 Skills Drawer（スコープ外の扱い）

codex タブでは ⚡ を disabled 表示（隠さない: 「この CLI では未対応」が伝わる方が発見性が良い）。codex の skills/plugin 機構への対応は roadmap に追記し、隔離ロード手段（`--plugin-dir` 相当）が確認できてから設計する。

---

## 6. エッジケース

| ケース | 挙動 |
|---|---|
| codex 未導入で ＋メニュー | Codex 項目グレーアウト（クリック不能）。agent-1 の claude フォールバック経路は現行のまま |
| ユーザー config.toml のパース失敗 | disable 群なしで spawn（rhino 追加のみ・既存サーバ共存）。`Log.Write` に記録。起動不能より共存を選ぶ |
| ユーザーが config.toml にサーバを追加した直後の spawn | spawn ごとに config.toml を読み直すので次の spawn から反映（キャッシュしない） |
| codex セッション ID 発見失敗 | タブは動作継続。resume 不可・レイヤマッピング欠落のみ。ログに記録 |
| codex タブ 2 つを同時作成 | claimed 集合で rollout の二重帰属を防止（5.2） |
| resume 対象の rollout が消えている | spawn 前に rollout 存在を再検証（claude の jsonl 再検証と同型）。消えていれば no-op + 一覧再送 |
| `codex delete` 失敗（exit != 0） | 一覧から消さずログ記録。タイムアウト 10s |
| UTC 日跨ぎ直後の spawn | rollout 探索は spawn 日と翌日の 2 ディレクトリを見る（5.2） |
| codex 側の `/new`（ID 回転） | claude の `/clear` と同じ既知の stale 問題として許容（roadmap の既存項目に含める） |
| Windows | codex の `where.exe` / `.cmd` shim は claude と同経路で解決。**ただし Windows 実機検証は claude 同様ゲート扱い**（roadmap） |

---

## 7. ドキュメント追従（同一作業内で必須）

- `README.md`: codex 対応の使い方・要件
- `docs/architecture.md`: 2 章に `Cli/` 追加、3.2 プロトコル表更新、7 章に D12–D16
- `docs/design-history.md`: D12–D16 の判断記録（本設計書から要約を移す）
- `docs/roadmap.md`: 「codex Skills Drawer 対応」「codex ID 回転追跡」「Windows 検証」を追記

---

## 8. スコープ外（明示）

- codex タブでの Skills Drawer / plugin 隔離ロード（→ roadmap）
- 初回タブ CLI の設定化・記憶（常に claude で固定）
- `codex fork` / `archive` の UI 対応
- CODEX_HOME 分離方式（不採用の判断は D13 に記録）
