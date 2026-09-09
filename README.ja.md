# CoRhino

<p align="center">
  <img src="src/Resources/icon.png" alt="CoRhino" width="120">
</p>

<p align="center">
  <a href="README.md">English</a> | 日本語
</p>

Rhino に **AI コーディングエージェントをドックする** Rhino プラグイン（Rhino 8 / Rhino 9 Beta 対応）。サイドパネルに常駐したエージェント（Claude Code / Codex CLI、タブごとに選択）が、いま開いているその Rhino を操作できる。

非エンジニアでも「パネルを開くだけ」で使えることを目標にしており、`MCPConnect` / 設定ファイルの貼り付け / 手動の MCP 起動を一切踏ませない。Rhino 操作の実体（tools / agents）は公式 [Rhino-MCP-Platform](https://github.com/mcneel/RhinoMCP) に乗り、CoRhino はその**パネル UX・起動配線・オンボーディング**だけを担う（ツールは持たない）。

https://github.com/user-attachments/assets/661a35e1-c61b-4d1d-92d9-56ed656b872f

> [!WARNING]
> Pre-alpha / WIP。コマンド・プロトコル・設定は予告なく変わる。

## 仕組み

```
Rhino プロセス
├── CoRhino プラグイン (.rhp)
│    └── サイドパネル = WebView + xterm.js（タブ式）
│         └── タブごとにエージェント CLI (claude / codex) を PTY で起動
│              └── MCP を direct-HTTP で接続 → http://localhost:<port>/
└── Rhino-MCP-Platform プラグイン (.rhp)
     └── http://localhost:<port>/ で Rhino 操作ツールを提供する MCP ホスト
```

- パネルごとに**専用ポートを採番**（base 10500）。複数ドキュメントでも各パネルが自分の Rhino を操作する。
- エージェントは Rhino-MCP-Platform のホストに **direct-HTTP** で直結する。node router は挟まない。
- エージェントセッションは**隔離**して起動する。タブ専用の MCP 設定を起動時にのみ注入し、`~/.claude` / `~/.codex` は汚さない（再ログイン不要）。

## 必要環境

- **Rhino 8.32 以降**、または **Rhino 9 Beta** — 推奨は macOS（Apple Silicon）。Intel Mac / Windows でも動作する
  - パッケージは 1 本で両対応。配布タグはフロア（下限）なので、`rh8_32` パッケージがそのまま Rhino 9 にも入る
- **[Claude Code](https://claude.com/claude-code) CLI**（`claude`）が PATH 上にあること
  - 見つからない場合はタブ内にセットアップビューが出て、公式インストーラをボタン一つで実行できる
- （任意）**[Codex CLI](https://developers.openai.com/codex/cli)**（`codex`）**0.142 以降** — あれば「＋」メニューから codex タブを起動できる。無ければグレーアウトされるだけで claude 側に影響はない
  - 古いバージョンでは codex タブの起動自体が失敗する（未知のフックフラグ）。`codex update` で解消
- **Rhino-MCP-Platform**（yak パッケージ）— 未導入ならパネルが検出し、同意ダイアログ → インストール → 再起動案内まで自動で行う

## インストール

Rhino のパッケージマネージャからインストールする。

1. Rhino で `_PackageManager` を実行（Tools ▸ Package Manager）
2. **CoRhino** を検索してインストール
3. Rhino を再起動

ソースからビルドする場合は[開発者向け](#開発者向け)を参照。

## 使い方

1. Rhino のコマンドラインで `CoRhino` を実行 → サイドパネルが開く
2. 初回、Rhino-MCP-Platform が無ければ導入の同意ダイアログが出る → インストール後に Rhino 再起動
3. パネル内のエージェントに「今のドキュメントに何がある？」のように指示する

タブ内で `/exit`（または `Ctrl-D`）するとそのエージェントが後始末されてタブが閉じ、**最後のタブが終了するとパネルも自動で閉じる**。もう一度 `CoRhino` を実行するとパネルが再び開き、新しいセッションが起動する（インスタンスの MCP ホスト・ポートは再利用される）。パネルが開いている間に実行した場合は前面に来るだけ。

パネル UI・ダイアログ・生成されるテンプレートは Rhino の言語設定に追従する（日本語 Rhino なら日本語、それ以外は英語）。

## 機能

### 並列エージェント（タブ）

タブバーの「＋」で、**同じ Rhino ドキュメントに対して**エージェントをもう1つ起動できる（claude / codex 混在可）。各タブは独立したセッションで、専用のレイヤ接頭辞（`s-XXXXXXXX/`）が割り当てられる。各エージェントは自分のレイヤ配下にだけ書き込み、他レイヤは読み取りのみとする規約で動くので、並列で別々の作業（例: 片方がジオメトリ生成、片方が解析）を進めても衝突しにくい。タブの「×」でそのエージェントだけ終了できる。

CLI バッジ（● claude / ◆ codex）の隣の**ステータスグリフ**で、タブを開かずに状態が分かる: `✻`（アンバー・回転）= 稼働中、`‼`（赤・点滅）= **承認待ち**、非アクティブ中に完了すると `✓`（緑・未読）、待機中は表示なし。

> 注意: レイヤ分離は CLAUDE.md / AGENTS.md による**規約**であり、ホストが強制するわけではない。同じオブジェクトをレイヤ跨ぎで触ると論理衝突し得る。また各エージェントは互いの変更をリアルタイムには知らない。

### セッション管理（🕘）

会話は CLI が自動保存している。🕘 ドロワーで過去セッションを一覧（タイトル自動生成・日時つき）し、ワンクリックで再開できる。タブラベルのダブルクリック（または一覧の ✎）で名前を付けられ、再開後も引き継がれる。削除は 🗑（2度押しで確定）。全セッションが共有ワークスペース（`~/.corhino/workspace/`）で動くので、Rhino を再起動しても履歴が繋がる。claude / codex 両方のセッションが CLI バッジ付きで 1 つのタイムラインに並び、クリックすれば該当 CLI で再開される。

### 自作スキル（⚡）

自作スキルは `~/.corhino/skills/`（初回起動時に骨格と書き方 README を自動生成）に置き、**両 CLI の全セッション**に読み込まれる。パネル内の claude に「今の手順をスキルにして」と頼むと、同梱の `save-rhino-skill` メタスキルが規約どおり `skills/<name>/SKILL.md` を書き出す（手で置いてもよい）。

⚡ ドロワーでスキル一覧（自作/同梱バッジ・説明）をターミナル横に表示できる。保存直後のスキルはライブで現れ、未反映のセッションには ⚠ が付く。反映方法は CLI で異なり、claude タブはヘッダの ⟳ 一発、codex は実行中セッションへの再読込ができないため新しいタブから有効になる。

### エージェントオーケストレーション

タブ内のエージェント自身が、**別のエージェントのタブを作成して作業を委譲**できる。「この作業を別のエージェントに任せて」のように頼むと、同梱スキル `orchestrate-agents` の手順で子タブを spawn し、指示を送り、完了を検知して成果を回収し、子タブを閉じる。

- 追加の承認ダイアログは出ない。spawn を駆動する Bash 実行自体が親エージェントの権限モードで既にゲートされているため。子タブの出現と Rhino コマンドライン通知で可視化される（`acceptEdits` 等の緩い権限モードが指定された場合は警告付き）
- エージェントが操作できるのは**自分が作った子タブだけ**。ユーザーが開いたタブには触れない
- 子タブはフォーカスを奪わず背景で作られ、進行状況はタブのステータスグリフで分かる
- spawn で生まれたセッションは 🕘 一覧で `⧉` バッジと「↳ 親名 から spawn」付きで表示される
- タブ総数は 8 個まで。V1 で spawn できる子は Claude Code のみ

### 設定（⚙）

⚙ ドロワーにパネルのユーザー設定がある（`~/.corhino/settings.json` に保存）。どちらも**新しいタブから**適用される（実行中のセッションには効かない——タブのサンドボックスは spawn 時に固定されるため）。

**サンドボックス**（デフォルト OFF）。ON にすると、エージェントのコマンド実行（Bash）が OS レベルで隔離され、ファイルの書き込み先が `~/.corhino` 内に制限される。範囲外への正当な操作は通常の承認フローに戻るだけ。**Rhino モデルの操作（MCP 経由）には影響しない**——モデルの保護は従来どおりレイヤ規約と Undo が担う。

- **macOS のみ対応**（Claude Code 組み込みの Seatbelt サンドボックス。Windows ではトグルが無効表示）
- 対象は **Claude Code タブのみ**。codex タブは設計として常時サンドボックス（`workspace-write`、承認プロンプトなしとペア）で動く

**Codex のネットワーク**（デフォルト OFF）。codex タブは常時サンドボックスで、そのサンドボックスはネットワークも塞ぐ。ON にすると `sandbox_workspace_write.network_access=true` を渡し、codex タブのコマンド実行が外部へ接続できるようになる。

- 対象は **codex タブのみ**（Claude Code には影響しない）
- このフラグが握るのは外部通信だけではない。codex はローカルの Unix ソケットと `com.apple.*` の mach サービス（`com.apple.SecurityServer` を含む）を同じスイッチに束ねているため、OFF だと **macOS キーチェーンに到達できず**、キャッシュ済みの認証情報が「無い」ものとして扱われる。実際の codex サンドボックス内で計測済み（`docs/design-history.md` D27）
- **1Password CLI（`op`）は ON にしても動かない**。`op` はデスクトップアプリへ `2BUA8C4S2C.com.1password.browser-helper` への XPC で接続するが、codex の mach-lookup 許可リストは `com.apple.*` のみで、どちらの状態でも拒否される（設定で開ける手段は無い）。鍵をキーチェーンにキャッシュする CLI（`comfyctl` など）は、通常のターミナルで一度キャッシュを温めておけば、有効期限内は codex タブからそれを使える
- ON = codex のシェルコマンドが承認プロンプトなしで外部へ出る（codex タブは `-a never`）。知った上で opt-in すること

## 開発者向け

### ビルド

UI（vite）のビルドは .NET ビルドに統合済み。**1コマンド**で完結する。

```bash
make build     # = dotnet build src/CoRhino.csproj
```

- `BuildUi` ターゲットが `ui/` で `npm install`（初回のみ）+ `npm run build` を実行
- 成果物を `web/` として `.rhp` の隣に配置し、PostBuild が **すべての** Rhino plugin ディレクトリへコピー
  - macOS: `~/Library/Application Support/McNeel/Rhinoceros/{8.0,9.0}/MacPlugIns/CoRhino` — plugin ディレクトリは Rhino のメジャーバージョンごとに分かれるため、1 回のビルドで両方に入れる
  - Windows: `%APPDATA%\McNeel\Rhinoceros\myplugins\CoRhino` — バージョン共通なので 1 箇所で両対応
- macOS は arm64/x64 の native dylib を `lipo` でユニバーサル化して同梱（片方だけだと dlopen 失敗）

> コピー先（plugin ディレクトリ）を直接編集しないこと。常に再ビルドで反映する。

### 開発コマンド

主要コマンドは `Makefile` に集約している（`make help` で一覧）。

| target | 内容 |
|---|---|
| `make build` / `make release` | Debug / Release ビルド（UI ビルド + native flatten + plugin dir へインストールまで込み） |
| `make ui-dev` / `make ui-build` | vite dev サーバ / UI 単体ビルド |
| `make pack` | Release ビルド + yak パッケージ生成（`dist/yak/`）。`make pack VERSION=0.1.0` で版指定 |
| `make yak-login` / `make yak-push-test` / `make yak-push` | yak 認証 / テスト・本番サーバへ push |
| `make clean` / `make uninstall` | ビルド成果物削除 / インストール済みプラグイン削除 |

UI を vite dev サーバから読ませる場合は、`make ui-dev` 実行中に環境変数を設定してから Rhino を起動する:

```bash
export CORHINO_WEBUI_URL=http://localhost:5173
```

設定時はパネルがディスク上の `web/index.html` ではなく dev サーバの URL を読む。

### プロジェクト構成

```
src/            C# Rhino プラグイン
├── Panel/      パネル本体（オーケストレータ）、ドロワー、オーケストレーションプロトコル、設定永続化
├── Cli/        CLI アダプタ層（IAgentCli / ClaudeCli / CodexCli — 起動引数・隔離・ID 発見）
├── Pty/        PTY セッション（Porta.Pty）と CLI 解決
└── Commands/   "CoRhino" コマンド（パネルを開く）
cc-plugin/      同梱 Claude Code プラグイン（save-rhino-skill / orchestrate-agents）
ui/             パネル UI（TypeScript + xterm.js, vite）
yak/            パッケージ manifest.yml + icon
build-yak.sh    .yak パッケージを組み立てるスクリプト
Makefile        主要コマンドの集約（make help で一覧）
```

### パッケージング（yak）

`.yak` パッケージにはビルド出力（`.rhp` + 依存一式）をそのまま同梱する。ローカルの実動インストールと同じファイル集合になる。

```bash
make pack                 # Release ビルド → dist/yak/corhino-<ver>-rh8_32-<os>.yak
make pack VERSION=0.1.0   # パッケージバージョンを明示指定
```

（`make pack` は `build-yak.sh` を呼ぶだけ。スクリプトを直接叩いても同じ。）

- パッケージは **OS ごとに作る**（native pty ライブラリが OS 依存）。mac 用は macOS で、win 用は Windows（Git Bash / WSL）でビルドする。
- 生成物は `dist/yak/`（gitignore 対象）。manifest / icon は `yak/` にある。
- 手動公開は `make yak-login` → `make yak-push-test`（テスト）→ `make yak-push`（本番）。

### リリース自動化（GitHub Actions）

通常のリリースは CI に任せる（[.github/workflows/release.yml](.github/workflows/release.yml)）。

- **`v*` タグを push** → macos / windows ランナーで両 OS の `.yak` をビルドし、**本番サーバへ push**。タグと csproj の `<Version>` が一致しないと fail する（バージョンの単一の真実は csproj のまま）。
- **workflow_dispatch（手動実行）** → 同じビルドを**テストサーバ**（毎晩ワイプ）へ push。リリースパイプラインのリハーサル用。

```bash
# リリース手順: csproj の <Version> を上げてコミット → タグを push するだけ
git tag v0.2.0 && git push origin v0.2.0
```

仕組み上のポイント:

- プラットフォームタグ（`-mac`/`-win`）は `yak build --platform` で**パッケージに焼き込まれる**ので、push 自体は 1 つの linux ジョブに集約できる（ビルドだけ OS 別ランナーが必要）。
- yak CLI は[スタンドアロン配布](https://files.mcneel.com/yak/tools/0.13.0/)を使うため、ランナーに Rhino のインストールは不要。コンパイル参照（RhinoCommon / Rhino.UI / Eto）も NuGet の RhinoCommon パッケージが供給する。
- **初回セットアップ（一度だけ）**: 手元で `yak login --ci` を実行すると**失効しない** API キーが発行されるので、リポジトリの Secrets に `YAK_TOKEN` として登録する（yak は環境変数 `YAK_TOKEN` から自動で読む）。通常の `yak login` のトークンは約 30 日で切れるので CI には使わない。

### 対応 Rhino 最小バージョン（配布タグ）

yak は `.rhp` が参照する **RhinoCommon のバージョン**を配布タグ（例 `rh8_32` = Rhino 8.32 以上）として刻む。開発機にインストールされた Rhino のバージョンに追従してフロアが勝手に変わらないよう、`src/CoRhino.csproj` の `RhinoCommonVersion` で **NuGet の RhinoCommon を固定参照**している（`ExcludeAssets="runtime"` = 参照専用。実体は Rhino が実行時に供給する）。

- 現在のフロア: **rh8_32**（`8.32.26160.13001`、最新）。
- 固定できる下限は **rh8_19**（`8.19.25132.1001`）。RhinoCommon NuGet が CoreCLR（net7.0）アセンブリを同梱するのは 8.19 以降で、8.18 以前は net48 のみ＝net8.0 ビルドで復元できない。
- 古い Rhino 8 まで対応を広げたいときは `RhinoCommonVersion` を下げる（下限 8.19）。

タグは **pin ではなく floor（下限）**。`rh8_32` は Rhino 9 にもそのまま入る（検証済み: Rhino 9 Beta の yak で `.../Rhinoceros/packages/9.0/corhino` にインストールされる）。したがって Rhino 9 用の**別パッケージは不要**。Rhino 9 専用の RhinoCommon API が必要になったときだけ `rh9_*` ビルドを追加する。

## ライセンス

[MIT](LICENSE)。同梱するサードパーティ（Porta.Pty / xterm.js — いずれも MIT）の表示は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) にまとめており、両ファイルとも `.yak` パッケージに同梱される。
