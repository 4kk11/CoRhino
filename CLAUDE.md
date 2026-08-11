# CoRhino

Rhino（8 / 9 Beta）にドックした Claude Code パネル（C# プラグイン `src/` + xterm.js UI `ui/`）。
PTY で `claude` を起動し、公式 Rhino-MCP-Platform のホストに direct-HTTP で接続させる。

## プロジェクトステータス: Pre-alpha / WIP

まだ設計を固めている段階。**後方互換より設計上の正しさを優先し、破壊的変更を厭わない。**

- コマンド名・パネル/ワークスペース構成・`.mcp.json` の形・環境変数名・CLI 配線は、設計上正しいなら自由に変更してよい
- 既存ユーザー・既存セッションへの配慮で設計を曲げない。移行措置や互換シムは原則作らない
- deprecated 経路を残さない。不要な分岐は自律操作の失敗原因になる
- ただし変更したら同じ作業内でドキュメント（下記）を必ず追従させる

## 回答スタイル

- 挨拶・前置き・段階報告は不要。結論ファースト
- 指摘すべきことは率直に指摘する
- 不明点がある場合は必ず確認する

## コマンド

主要コマンドは `Makefile` に集約（`make help` で一覧）。

- Build（UI ビルド統合済み・これ一発で完結）: `make build`（= `dotnet build src/CoRhino.csproj`）
- UI 単体開発: `make ui-dev`（`CORHINO_WEBUI_URL=http://localhost:5173` をセットして Rhino 起動）
- yak パッケージ: `make pack` → `make yak-push`（配布の詳細は README「Packaging (yak)」「Release automation」節）

## 設計境界（崩さない）

- CoRhino の責務は **パネル UX + 起動配線 + オンボーディング** のみ。Rhino 操作の tools/agents は
  公式 Rhino-MCP-Platform に乗せ、**こちらにツールを実装しない・vendoring しない**
- 接続は **direct-HTTP**（`http://localhost:<port>/`）。node router は使わない
- ポートは **パネル単位で採番**（base 10500）。「どの Rhino を操作するか」をインスタンスに紐付ける責務分離を崩さない
- MCP ホスト起動は `RHINO_MCP_AUTOSTART_PORT` + `_-MCPSpawn`。`_-MCPStart <port>`（対話入力）は
  runscript と相性が悪く起動失敗するので使わない
- claude は `--strict-mcp-config --mcp-config <session>/.mcp.json` で隔離起動。`~/.claude` を汚さない

## ドキュメント追従

ソースを変えたら同じ作業内で関連ドキュメントを更新する。

- CLI/コマンド・配線・要件の変更 → `README.md`（英語）と `README.ja.md`（日本語）。**両方を同内容に保つ**
- アーキテクチャ（現在の構造・how）→ `docs/architecture.md`
- xterm⇄PTY 入出力フローの細部 → `docs/details/io-flow-xterm-pty.md`（深掘り資料は `docs/details/` に置く）
- 設計判断・検証履歴・バグ記録（なぜ・過去）→ `docs/design-history.md`
- 未実装・今後の作業（これから）→ `docs/roadmap.md`

各 docs の単一責務（how / why / future）を崩さない。重複しそうな内容は片方に集約しリンクで繋ぐ。

## コミット

gitmoji 形式（例: `✨ feat: ...`）。タイトル・本文は英語。

- NEVER: ユーザーの明示的な承認なしにコミット・プッシュしない
- NEVER: コミットメッセージに `Co-Authored-By` や `Generated with` 等のシグネチャを付けない

## 注意事項

- 本リポジトリは my-life の submodule（`.06_Projects/CoRhino`）。変更は submodule 側で commit/push し、親の gitlink を更新する
- NEVER: Rhino の plugin ディレクトリにコピーされた成果物を直接編集しない。必ず再ビルドで反映する
- macOS は arm64/x64 の native dylib を `lipo` でユニバーサル化して同梱している（片方だけだと dlopen 失敗）
- macOS の plugin ディレクトリは Rhino のメジャーバージョンごと（`Rhinoceros/8.0/` と `9.0/`）。ビルドは両方に配置する。
  対応 Rhino を増やすときは csproj の `PluginDest` アイテムと Makefile の `PLUGIN_DIR_*` を揃えて追加する
