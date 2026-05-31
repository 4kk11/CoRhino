# Phase 0 スパイク手順書

目的: 製品コードを書く前に、direct-HTTP 配線の不確定点(U1〜U4)を実機で潰す。
所要 15〜30分。あなたが Rhino とターミナルを手で動かす検証。

## 前提セットアップ（1回だけ）

1. **Rhino 8 を起動**し、適当な箱を1つ置く（`Box` コマンド）。読み取り対象を作るため。
2. **Rhino-MCP-Platform を導入**（どちらか）:
   - Rhino で `PackageManager` → "Rhino-MCP-Platform" を検索してインストール → **Rhino 再起動**
   - または: `/Applications/Rhino 8.app/Contents/Resources/bin/yak install Rhino-MCP-Platform` → Rhino 再起動
3. Rhino で **`MCPStart`** を実行 → ポートは Enter で既定 `10500`。
   コマンドラインに `MCP server currently running on http://localhost:10500/` 相当が出ればOK。
4. ターミナルで疎通確認:
   ```
   curl -s -X POST http://localhost:10500/ \
     -H 'content-type: application/json' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | head -c 400
   ```
   → ツール定義のJSONが返れば HTTP サーバ生存。空/接続拒否ならポートかMCPStartを見直す。

このリポジトリの `rhino-http.json` を使う（`{"rhino":{"type":"http","url":"http://localhost:10500/"}}`）。
以下 `SPIKE=/Users/akihito/Documents/my-life/.06_Projects/CoRhino/docs/spike`、
`CCPLUGIN=/Users/akihito/Documents/my-life/.06_Projects/rhino-cli/references/RhinoMCP/cc-plugin` とする。

---

## S1 — direct-HTTP で claude が Rhino を読めるか（U1, U2）

```
cd /tmp
claude --strict-mcp-config --mcp-config "$SPIKE/rhino-http.json"
```

claude 起動後:

1. `/mcp` を実行 → `rhino` が **connected** で、ツール一覧が見えるか。
2. ツール名の名前空間を確認 → **`mcp__rhino__list_objects`** 形式か（`mcp__plugin_...` でないこと）。
3. プロンプトに「今の Rhino ドキュメントに何があるか教えて」→ 箱を1つ報告するか。
4. 「球を1つ足して」→ 実際に Rhino に球が増えるか。

**判定**
- [ ] rhino が connected
- [ ] ツール名は `mcp__rhino__*`
- [ ] 読み取り成功（箱を認識）
- [ ] 書き込み成功（球が増える）

S1 が全部通れば **direct-HTTP 配線(D1)は成立**。S2 の答え(=`mcp__rhino__*`)もここで確定。

---

## S3 — 公式 cc-plugin の agents/commands が http の rhino に繋がるか（U1, U3, U4）

統合の核心。cc-plugin を `--plugin-dir` でロードしつつ、rhino サーバは http に固定する。

### S3a: strict で http のみ + プラグインの agents/commands

```
cd /tmp
claude --plugin-dir "$CCPLUGIN" --strict-mcp-config --mcp-config "$SPIKE/rhino-http.json"
```

1. `/help` か `/plugin` → `rhino-mcp` プラグインが認識され、`/rhino-mcp:scene`
   `/rhino-mcp:snapshot` コマンドが見えるか。
2. `/mcp` → rhino が **http で connected**（node router ではない）か。
   ※ `--strict-mcp-config` が plugin 同梱の node 版 rhino を抑止できているかの確認。
3. `/rhino-mcp:scene` を実行 → `mcp__rhino__list_objects` を呼んで箱を報告するか。
4. 「球を足して」等の地物タスク → `rhino-modeller` エージェントが自動起動し、
   `mcp__rhino__run_python` 等を呼んで成功するか。

**判定**
- [ ] cc-plugin の commands/agents がロードされた
- [ ] rhino は http 接続（node 不要で動いた）
- [ ] `/rhino-mcp:scene` が箱を報告
- [ ] rhino-modeller が地物編集に成功

### S3b（S3a で strict が agents まで消したら）: strict 無しで precedence 確認

`--strict-mcp-config` が plugin の agents/skills まで無効化してしまう場合や、
plugin の node rhino とこちらの http rhino が衝突する場合に、どちらが勝つかを見る:

```
claude --plugin-dir "$CCPLUGIN" --mcp-config "$SPIKE/rhino-http.json"
```

- `/mcp` で rhino が http か node かを確認（precedence の実測 = U3/U4 の答え）。
- node 版が勝つ場合、node 未導入だと rhino が落ちるはず → その挙動も記録。

---

## 記録テンプレート（結果をここに追記 → plan に反映）

```
## スパイク結果 YYYY-MM-DD

S1: pass/fail
  - rhino connected: 
  - tool namespace: mcp______
  - read: 
  - write: 

S3a (strict + plugin-dir): pass/fail
  - commands/agents loaded: 
  - rhino transport (http/node): 
  - /rhino-mcp:scene: 
  - rhino-modeller: 

S3b (precedence): 
  - winner when both define rhino: http / node

結論:
  - direct-HTTP 成立: yes/no
  - cc-plugin を --plugin-dir + --mcp-config(http) + (strict?) で乗れる: yes/no
  - Phase 1 で使う確定コマンドライン: claude ____
  - 残課題: 
```

## トラブル時の切り分け

- **rhino が connected にならない**: `curl` 疎通(前提4)に戻る。url の末尾スラッシュ有無を
  `http://localhost:10500`（無し）でも試す。MCPStart のポートを確認。
- **`--strict-mcp-config` で agents も消えた**: agents はMCPサーバではないので消えない想定。
  消えるなら S3b へ。
- **node 版 rhino が優先されて落ちる**: `--strict-mcp-config` を付ける。それでも plugin 版が
  残るなら U4 は「上書き不可」→ plan R1 の緩和策(cc-plugin の .mcp.json だけ差し替え)へ。
- **`--mcp-config` がインライン文字列を受けない**: 本キットはファイル渡しなので影響なし。
```
