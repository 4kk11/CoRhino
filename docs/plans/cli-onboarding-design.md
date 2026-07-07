# CLI セットアップビュー（未インストール時オンボーディング）設計メモ

対象: agent CLI（claude / codex）が PATH に無い状態でタブが開かれたときの UX。
規模が小さいため design/tasks を分けず本メモ 1 枚で完結させる。

## 意図（なぜ必要か）

現状、CLI 未解決のタブは黙って `/bin/bash -l`（Windows は `cmd.exe`）にフォールバックする。
初見のユーザーには「なぜかただのシェルが開いた」ようにしか見えず、理由は Rhino のログを
見ないと分からない。CoRhino は「パネル UX + 起動配線 + オンボーディング」が責務なので、
Rhino-MCP-Platform の install オンボーディング（D2 系）と同様、CLI 側も導線を持つべき。

## 決定

1. **シェルフォールバック廃止 → タブ内セットアップビュー**。
   CLI 未解決時は PTY を起動せず、タブのペインに HTML の案内（インストールコマンド +
   実行/再チェック/シェルを開く）を表示する。
   - ネイティブダイアログ案は不採用: MCP プラットフォームは「Rhino 側」の話でダイアログが
     適切だが、CLI は「パネル内」の話。パネル内で完結できるのに外に出す理由がない。
2. **インストール誘導は各 CLI の公式ネイティブインストーラ**（npm ではなく）。
   - Node 依存を持ち込まない・自動アップデートが付く（claude）・公式の第一推奨。
   - コマンドは表示＋コピー可能にし、実行はボタン起点のみ（勝手に走らせない）。
3. **インストール実行はそのタブの PTY で可視化**。
   `bash -lc '<installer>'`（win: `powershell -ExecutionPolicy Bypass -Command`）を
   タブの xterm にそのまま流す。exit 後に再 probe → 成功なら同じタブで CLI を起動、
   失敗ならエラーバナー（出力は読める位置に残す）。
4. **`CliResolver` に well-known ディレクトリのフォールバック**（下記の落とし穴対応）。

## 調査結果（2026-07-07 検証済み）

公式ネイティブインストール手段。URL は実在確認済み（HTTP 200）。

| CLI | macOS / Linux / WSL | Windows (PowerShell) | 配置先 |
|---|---|---|---|
| Claude Code | `curl -fsSL https://claude.ai/install.sh \| bash` | `irm https://claude.ai/install.ps1 \| iex` | `~/.local/bin/claude` |
| Codex | `curl -fsSL https://chatgpt.com/codex/install.sh \| sh` | `irm https://chatgpt.com/codex/install.ps1 \| iex` | `~/.local/bin/codex` |

- Claude Code はネイティブ版のみバックグラウンド自動アップデートあり。brew cask /
  winget / apt/dnf/apk / npm も公式配布だが、第一推奨はネイティブインストーラ。
- Codex は brew cask（`brew install --cask codex`）と npm もあるが、README はネイティブ
  インストーラを筆頭に載せる。
- 出典: code.claude.com/docs/en/setup, github.com/openai/codex README。

### 落とし穴: インストール直後に probe が失敗する

両インストーラとも `~/.local/bin` に置き、PATH の追記は**ユーザーのログインシェルの
プロファイル**（zsh なら `.zprofile`/`.zshrc`）に行う（codex の install.sh 実物で確認、
`pick_profile()`）。CoRhino の probe は `/bin/bash -lc` なので `.zprofile` を読まず、
**インストール成功直後でも `command -v` が外れる**。

→ 対策: `CliResolver.Resolve` は shell 解決に失敗したら `~/.local/bin/<bin>`
（win: `%USERPROFILE%\.local\bin\<bin>.exe`）を直接チェックする。この経路で解決した
場合、スポーン時の `PATH` にそのディレクトリを前置して子プロセスからも見えるようにする。

## UX フロー

```
タブ生成 → Resolve 失敗
  ┌──────────────────────────────────┐
  │        ● Claude Code が見つかりません          │  ← phase: missing（中央ボックス）
  │  PATH に claude がありません。公式インストーラ:  │
  │  ┌ curl -fsSL https://claude.ai/… ┐ [コピー]   │
  │  [インストールを実行] [再チェック]              │
  │  シェルを開く                                  │
  └──────────────────────────────────┘
[インストールを実行] → overlay 消灯、installer 出力が xterm に流れる（phase: installing）
  exit → 再 probe ─ 成功 → 同タブで CLI 起動（onSessionRestart で画面クリア）
                  └ 失敗 → 下端バナー（phase: failed。出力は読めるまま）
[再チェック] → probe のみ。成功 → CLI 起動 / 失敗 → failed バナー
[シェルを開く] → 従来のログインシェル（明示操作のときだけ）
```

- probe 成功で CLI を起動したら `PushCliAvailability` を再実行し、＋メニューの
  グレーアウトも同期する（従来は ready 時 1 回のみ）。

## プロトコル追加

| 方向 | メッセージ | 内容 |
|---|---|---|
| host→UI | `onCliSetup {agentId, phase, cli, command}` | `phase: 'missing' \| 'installing' \| 'failed'`。command は表示・コピー用のインストールコマンド |
| UI→host | `installCli {agentId}` | 公式インストーラをタブの PTY で実行 |
| UI→host | `recheckCli {agentId}` | 再 probe。成功なら同タブで CLI 起動 |
| UI→host | `openShellInTab {agentId}` | 明示的なシェル起動（旧フォールバックの移設） |

`onSessionRestart` は overlay の消灯＋画面クリアに再利用。ホスト側は PTY を作り直す
（`PtySession` はワンショット）ので、UI は overlay 消灯時に resize を再送する。

## 実装ポイント

- `IAgentCli.NativeInstallCommand`: プラットフォーム解決済みのコマンド文字列（表示と
  実行の両方に使う。乖離させない）。
- `AgentTab.CliSetupPhase`（null = 通常）/ `InstallerActive`。installer の exit は
  `OnSessionExited` で分岐し、RemoveTab ではなく再 probe フローへ。
- setup 状態のタブは PTY 無し（`IsRunning=false, Starting=false`）。パネル再オープンの
  `EnsureSessionAlive` はこれを dead 扱いで作り直すが、CLI が無ければまた setup に
  戻るだけなので許容。
- WebView が ready になる前に setup 状態へ入ったタブ（agent-1 が典型）は、`ready`
  ハンドラの AnnounceTab 後に `onCliSetup` を再送する。
- setup 状態の codex タブは `PendingCodexTabs` を保持しない（missing 分岐で除外、
  RelaunchCliInTab が `CreatedAtUtc` を現在時刻に張り直してから再登録）。setup に
  座り続けるタブが無関係な codex セッションの resume/delete をブロックしないため。

## レビュー（Codex ゲート・2026-07-07）

- R1: Medium×3 — ①overlay 消灯済みだと relaunch 時の resize 再送がスキップされ新 PTY が
  80x24 のまま ②`PushCliSetup` に stale-tab identity チェック無し（reopen 後の新 agent-1 に
  古い setup が届く）③setup 状態の codex タブが pending guard を保持し続ける。全て修正。
- R2: LGTM（remove→re-add の AsyncInvoke 順序・resize の spawn 前バッファリングまで確認済み）。
  ビルド 0W0E / tsc pass。
