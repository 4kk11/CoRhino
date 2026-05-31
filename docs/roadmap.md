# CoRhino ロードマップ — 未実装・今後の作業

このドキュメントの責務は「**これから／まだやっていないこと**」の一元管理だけ。
- 完了した判断・検証・バグ記録（なぜ今この形か）は [`design-history.md`](design-history.md)。
- 現在の構造そのものは [`architecture.md`](architecture.md)。

WIP / pre-alpha のため、ここの項目は予告なく増減・並び替えされる。

## 統合・配信

- **cc-plugin の agents/commands ロード（旧 S3 / U1 の残）**: `--plugin-dir` でローカル cc-plugin を
  ロードし、その agents/commands を CoRhino が定義した http `rhino` サーバに束縛できるかを実機判定する。
  現状は **agents 無し・素のツール直叩き**で E2E が成立しているので未着手。
- **plugin 配信の更新戦略**: marketplace 自動更新か submodule バンプかを確定する。

## オンボーディング

- **claude CLI 未導入時の導線整理（旧 Phase 2b）**: 現状は `claude` が PATH に無いと
  `bash -l` / `cmd.exe` へ無言フォールバックするだけ。導入を促す UX を決める。
- **パネル内の接続ステータス／インジケータ（旧 Phase 2c）**: 「Rhino-MCP 未接続」等を
  パネル内に表示する。現状はネイティブダイアログ＋コマンドラインで代替している。

## 堅牢性

- **ポート枯渇時のフォールバック UX**: `Workspace.ResolveFreePort` は 10500–10599 を試行し、
  100 個すべて埋まると 10500 を返すだけ（後段の bind エラーで顕在化）。明示的な UX が無い。
  （※「10500 使用中なら採番」自体は D6 で実装済み。ここは“全滅時”の話。）
