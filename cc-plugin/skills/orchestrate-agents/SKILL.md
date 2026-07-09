---
name: orchestrate-agents
description: ユーザーが「別のエージェントに任せて」「並列で作業して」「子エージェントを作って」「タブを分けて進めて」等、作業の委譲・並列化を求めたとき。または自分の判断で重い独立作業を切り出したいとき。CoRhino のコマンドファイル・プロトコルで子タブの作成・指示・完了検知・成果回収・クローズを行う
---

# orchestrate-agents

CoRhino パネル上で、あなた自身が別のエージェント（子タブ）を作成し、指示を出し、
完了を検知して成果を回収するためのプロトコル。C# パネルがコマンドファイルを監視して
実行する — あなたは JSON ファイルを書き、レスポンスとステータスファイルを読むだけ。

## 前提: 自分を知る

システムプロンプトに自分の **agentId**（例: `agent-3`）と **パネルポート**（例: `10500`）、
コマンド置き場とマニフェストのパスが注入されている。以下 `<port>` はそのポート。

- コマンド置き場: `~/.corhino/runtime/agent-commands/`
- レスポンス: `~/.corhino/runtime/agent-commands/responses/<id>.json`
- タブ一覧: `~/.corhino/runtime/tabs-<port>.json`（agentId・sessionId・レイヤ・親子関係・
  statusFile / transcript パスが常に最新で入っている）

## コマンドの書き方（共通）

1. `id` に uuid を生成する（`uuidgen | tr 'A-Z' 'a-z'`。**ファイル名と JSON の `id` は同一の
   uuid でなければならない** — 不一致はパネルが `invalid-request` で弾く）
2. JSON を **必ずコマンド置き場と同じディレクトリの一時名に書いてから mv** する。
   `/tmp` からの mv は別ファイルシステムだと copy になり、パネルが書きかけを読む:

```bash
id=$(uuidgen | tr 'A-Z' 'a-z')
dir=~/.corhino/runtime/agent-commands
cat > "$dir/.tmp-$id" <<EOF
{ "id": "$id", "from": "agent-3", "action": "spawnTab",
  "prompt": "あなたの担当レイヤに半径5の球を作成し、完了したら結果を報告してください" }
EOF
mv "$dir/.tmp-$id" "$dir/cmd-<port>-$id.json"   # 同一ディレクトリ内 rename = atomic
```

3. レスポンスをポーリングする（**読めたときだけ削除**。通常は 1–2 秒で返る）。
   **1回の Bash 実行は 90 秒以内に収める** — Bash ツールは
   既定 2 分で強制終了されるので、それより長い1本のループを書くとタイムアウト→再起動の
   オーバーヘッドを毎回払うことになる。短いループを「来るまで再実行」する:

```bash
res="$dir/responses/$id.json"
got=""
for i in $(seq 1 30); do
  [ -f "$res" ] && { cat "$res" && rm -f "$res"; got=1; break; }
  sleep 3
done
[ -z "$got" ] && echo "not yet — rerun this loop"
```

   数ループ過ぎても来ない場合はパネル側の異常（ポート違い・ファイル名と id の不一致等）を
   疑う。タイムアウトでレスポンスファイルを消してはいけない。

## アクション一覧

```json
// 子タブを作成し初期プロンプトを送る（配送はパネルが TUI ready を待って行う）
{ "id": "…", "from": "<自分>", "action": "spawnTab",
  "prompt": "<指示>",
  "permissionMode": "acceptEdits",   // 省略可: default | plan | acceptEdits | bypassPermissions
                                     // 緩和モードは Rhino コマンドラインに警告付きで通知される。
                                     // 必要なときだけ使い、通常は省略する
  "name": "builder-1" }              // 省略可: タブの表示名

// 稼働中の子に追加指示（busy 中でも送ってよい — claude が入力キューに積む）
{ "id": "…", "from": "<自分>", "action": "sendPrompt", "target": "<子のagentId>", "text": "<指示>" }

// 子を閉じる（作業が終わったら必ず片付ける）
{ "id": "…", "from": "<自分>", "action": "closeTab", "target": "<子のagentId>" }
```

spawnTab の成功レスポンスには観測に必要なパスが全部入っている:

```json
{ "ok": true, "action": "spawnTab", "agentId": "agent-4", "sessionId": "c247…",
  "layer": "s-c247bf6e", "statusFile": "/…/status/10500-agent-4",
  "transcript": "/…/.claude/projects/…/c247….jsonl" }
```

エラー語彙: `not-your-child`（自分の子以外は操作不可）/ `target-not-running` /
`tab-limit-reached`（パネル上限 8 タブ）/ `invalid-request` / `unsupported-cli`（V1 は claude のみ）。

## 完了を待つ

子の `statusFile` は `busy` / `waiting` / `idle` の1語。**「busy を一度見てから idle に
なったら完了」** — spawn 直後は idle で初期化されるので、初期 idle を完了と誤認しないこと。
状態監視のループも**1回の Bash 実行 90 秒以内**で刻んで再実行する（上記と同じ理由）。

**初期プロンプトの保険**: spawn から 60 秒経っても子が一度も `busy` にならない場合、
初期プロンプトの配送に失敗している可能性がある（パネル側も再送を試みるが保険は自分で持つ）。
同じ指示を `sendPrompt` で送り直す — 様子見で待ち続けない。

```bash
seen_busy=0
while :; do
  s=$(cat "<statusFile>" 2>/dev/null)
  [ "$s" = busy ] && seen_busy=1
  [ "$s" = waiting ] && echo "子が承認待ち（ユーザーの操作が必要かも）"
  [ "$s" = idle ] && [ $seen_busy = 1 ] && break
  sleep 3
done
```

`waiting` が長く続く場合、子がツール承認で止まっている。permissionMode を緩めていない
子では正常な状態 — ユーザーに伝えて待つ。

## 成果を回収する

idle は「ターン終了」であって「成功」ではない。必ず2段階で回収する:

1. **transcript から最終報告を読む**（jsonl の末尾から最後の assistant テキストを取る）:

```bash
tail -c 262144 "<transcript>" | grep '"role":"assistant"' | tail -1 \
  | python3 -c "import sys,json; m=json.loads(sys.stdin.read())['message']; \
      print(''.join(c.get('text','') for c in m['content'] if c.get('type')=='text'))"
```

2. **Rhino 側で裏取りする**: 子の担当レイヤ（レスポンスの `layer`）を対象に
   `list_objects` 等で、指示した成果物が実在するか確認してから次の指示 or closeTab に進む。

## ルール

- **数十秒で終わる単発作業に子を作らない** — 自分の rhino MCP（run_python 等）で直接やる方が
  圧倒的に速い（spawn・監視のオーバーヘッドは分単位）。委譲が効くのは**並列に進めたい
  作業・長時間かかる作業・コンテキストを分けたい作業**だけ
- **自分の子だけ** を操作する（他のタブへの sendPrompt/closeTab は `not-your-child` で弾かれる）
- 子への指示には **子自身の担当レイヤで作業させる**旨を含めなくてよい（レイヤはパネルが
  spawn 時に自動注入する）。ただし「どこに何を作るか」はレイヤ相対で明確に書く
- 作業が終わった子は **closeTab で片付ける**（タブ上限 8 を食い潰さない）
- レスポンスファイルは **読んだら消す**
- 子にさらに子を作らせる設計は避ける（監視の連鎖が深くなり状況把握が破綻する。並列化は
  自分から幅で広げる）
