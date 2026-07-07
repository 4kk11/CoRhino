# Skills Drawer（スキル一覧ビュー）設計書

| 項目 | 内容 |
|------|------|
| 対象 | CoRhino パネル（WebView UI + C#） |
| 作成日 | 2026-07-07 |
| ステータス | Draft |
| タスクリスト | [`skills-drawer-tasks.md`](skills-drawer-tasks.md) |

---

## 1. 概要

### 1.1 意図（なぜ必要か）

D8 でユーザーが会話からスキルを動的に蓄積できるようになったが、**何が溜まっていて・何がロード済みか**を確認する手段が会話しかない。特に「保存したのに発動しない（reload 忘れ）」は非エンジニアには原因が見えない。パネル UX はまさに CoRhino の責務なので、スキルの可視化と reload のワンクリック化をパネル側で提供する。

### 1.2 ユースケース

- 自分がこれまでに作ったスキルの一覧と「いつ使うか」を眺める
- save-rhino-skill で保存した直後、一覧に現れたことを確認する
- 「⚠ 要 reload」のスキルを見つけて、ボタン一発でセッションに反映する
- スキルの中身を直したくなったら、その場からエディタで開く

### 1.3 V1 スコープ外

- スキルの削除・新規作成 UI（削除はエディタ/Finder で、作成は save-rhino-skill で足りる）
- SKILL.md 本文のパネル内プレビュー・編集
- Platform 配布プラグイン（将来 3 本目の `--plugin-dir`）の表示 — 実装時は `source` を増やすだけ

---

## 2. UI レイアウト

### 2.1 全体構造

タブバー右端にトグルボタン「⚡」を追加し、ターミナルの右に幅 260px のドロワーを開閉する。
ターミナルと同時に見えることが要件（一覧を見ながら claude に依頼できる）。

```
┌──────────────────────────────────────────────┐
│ agent-1 │ agent-2 │ +                    │ ⚡ │  ← tabbar（⚡は右端固定）
├─────────────────────────────┬────────────────┤
│                             │ Skills       ⟳ │  ← drawer header
│                             ├────────────────┤
│                             │ ● facade-louver│
│   xterm (claude)            │   [自作]        │
│                             │ ● save-rhino-… │
│    (FitAddon が開閉時に      │   [同梱]        │
│     リサイズを吸収)          │ ⚠ new-skill    │
│                             │   [自作] 要reload│
│                             ├────────────────┤
│                             │ (空のときヒント) │
└─────────────────────────────┴────────────────┘
```

### 2.2 ドロワー各部

| 部位 | 内容 | 操作 |
|------|------|------|
| ヘッダー | タイトル「Skills」+ ⟳ ボタン | ⟳ = アクティブタブへ `/plugin reload-plugins` を注入 |
| スキル行 | 状態ドット + name + 出所バッジ | クリックで description 展開 ⇄ 折りたたみ |
| 展開部 | description 全文 + 「エディタで開く」リンク | リンク = OS デフォルトエディタで SKILL.md を開く |
| 空状態 | 「まだスキルがありません。claude に『今の手順をスキルにして』と頼むと保存されます」 | — |

### 2.3 スキル行

```
┌────────────────────────────┐
│ ⚠ facade-louver     [自作] │   ← 状態ドット / name / 出所バッジ
│   要 reload                │   ← needsReload のときのみ
├────────────────────────────┤   ← クリックで展開
│  ルーバーを生成するとき。    │
│  ピッチ・角度・基準サーフェ  │
│  スを確認してから…          │
│  ↗ エディタで開く           │
└────────────────────────────┘
```

| 表示 | 意味 |
|------|------|
| `●`（緑） | ロード済み（アクティブタブの spawn/最終 reload より古い） |
| `⚠`（黄） | 要 reload（spawn/最終 reload 後に追加・変更された） |
| `[自作]` | `~/.corhino/skills/` 由来 |
| `[同梱]` | `<plugin dir>/cc-plugin/` 由来（CoRhino 更新でしか変わらないので原則 `●`） |

---

## 3. データモデル

### 3.1 SkillInfo（C# → UI の DTO）

```ts
type SkillInfo = {
  name: string          // frontmatter name（無ければディレクトリ名にフォールバック）
  description: string   // frontmatter description（無ければ空文字 → UI で「(説明なし)」）
  source: 'user' | 'bundled'
  path: string          // SKILL.md の絶対パス（openSkill に渡す）
  needsReload: boolean  // アクティブタブの skillsLoadedAt と mtime の比較（C# 側で計算）
}
```

### 3.2 needsReload の意味論

- 各 `AgentTab` に `SkillsLoadedAtUtc` を持たせる。**spawn 時に初期化**し、ドロワーの ⟳ で
  reload を注入したとき **now に更新**する。
- `SKILL.md の mtime > アクティブタブの SkillsLoadedAtUtc` → `needsReload = true`。
- タブごとに spawn 時刻が違うため、ドロワーは常に**アクティブタブ視点**の状態を表示する
  （タブ切替時に再計算・再描画）。
- 既知の限界（V1 で許容）: 削除済みだが claude 内にまだロードされているスキルは一覧に出ない。
  `/plugin reload-plugins` を claude 側で手打ちされた場合は検知できず ⚠ が残る（⟳ を押せば解消）。
  逆に、claude が応答ストリーミング中や入力途中に ⟳ を押すと、注入したコマンドが入力バッファに
  混ざって実行されないまま `SkillsLoadedAtUtc` だけ進み、**⚠ が誤って消える**（false ●）ことがある。
  復帰はもう一度 ⟳（プロンプト待ちで押す）。実行確認を取るには claude の出力解釈が必要になり
  V1 の複雑さに見合わない。
  また、ドロワー表示中にアクティブタブの claude が死ぬと直前の一覧が残るが、最後のタブの終了は
  パネルごと閉じるため一瞬で、実害はない。

---

## 4. メッセージプロトコル拡張

既存の JSON プロトコル（Web→C#: `postMessage` / C#→Web: `window.CoRhinoPanel?.onXxx(...)`）に追加する。

### 4.1 Web → C#

| type | ペイロード | 処理 |
|------|-----------|------|
| `listSkills` | `{ agentId }` | スキャン結果を `onSkillsList` で返す（agentId 視点の needsReload） |
| `openSkill` | `{ path }` | SKILL.md を OS デフォルトアプリで開く（`Process.Start` + `UseShellExecute`）。**パスは受け取った値をそのまま開かず、`SkillsCatalog` が直近スキャンで返した path 集合に含まれるか検証する**（WebView からの任意パス起動を防ぐ） |
| `reloadSkills` | `{ agentId }` | 対象タブの PTY に `"/plugin reload-plugins\r"` を書き込み、`SkillsLoadedAtUtc = now` に更新し、`onSkillsList` を再送 |

### 4.2 C# → Web

| 関数 | ペイロード | タイミング |
|------|-----------|-----------|
| `onSkillsList(json)` | `{ agentId, skills: SkillInfo[] }` | `listSkills`/`reloadSkills` への応答、および watcher 検知時（ドロワー開時のみ） |

`reloadSkills` を UI からの `stdin` 直叩きにしない理由: reload の事実を C# が知らないと
`SkillsLoadedAtUtc` を進められず、⚠ が消えない。副作用（PTY 書き込み + 時刻更新 + 再送）を
C# 側で 1 メッセージに束ねる。

### 4.3 UI が listSkills を送るタイミング

- ドロワーを開いたとき
- ドロワーが開いている間の: タブ切替時 / `onSkillsList` 以外に watcher 由来の push を受けたとき（→ 4.2 により push 自体が最新リストなので追加要求は不要）

---

## 5. C# 設計

### 5.1 ファイル構成

```
src/Panel/
├── SkillsCatalog.cs      # 新規: スキャン + frontmatter パース + SkillInfo JSON 化（UI 非依存）
├── CoRhinoPanel.cs       # 変更: メッセージ 3 種、FileSystemWatcher、AgentTab.SkillsLoadedAtUtc
└── Workspace.cs          # 変更なし（UserSkillsRoot を SkillsCatalog が参照）
```

### 5.2 SkillsCatalog（新規・単一責務）

```csharp
static class SkillsCatalog
{
    // user: ~/.corhino/skills/skills/*/SKILL.md
    // bundled: <assembly dir>/cc-plugin/skills/*/SKILL.md
    public static IReadOnlyList<SkillInfo> Scan(DateTime skillsLoadedAtUtc);
    public static string ToJson(string agentId, IReadOnlyList<SkillInfo> skills);
    public static bool IsKnownSkillPath(string path);  // openSkill のパス検証
}
```

- **frontmatter パース**: 先頭が `---` 行なら次の `---` 行までを読み、`name:` / `description:` の
  単一行値だけ拾う（YAML ライブラリは入れない。複数行値は V1 非対応 → description が
  frontmatter に無い扱いになるだけで壊れない）。
- 読めないファイル・frontmatter 無しはフォールバック（name=ディレクトリ名, description=""）し、
  `Log.Write` してスキップしない（一覧から消える方が混乱する）。
- bundled 側はプロセス中に変化しないので初回スキャンをキャッシュしてよい（V1 は毎回スキャンでも実害なし）。

### 5.3 CoRhinoPanel の変更

- `AgentTab` に `SkillsLoadedAtUtc`（spawn 時に `DateTime.UtcNow`）。
- `FileSystemWatcher`（`~/.corhino/skills/skills/` を再帰監視、Created/Changed/Deleted/Renamed）:
  - パネルのコンストラクタで生成、`Dispose` で破棄（doc イベント解除と同じ場所）。
  - **300ms デバウンス**（エディタは保存で複数イベントを発火する）。
  - 発火時: ドロワーが開いているときだけ `onSkillsList` を push（UI から `drawerOpen` 状態を
    `listSkills`/ドロワー close 通知で受けるのではなく、**push は常に行い UI 側で drawer 閉時は
    無視**する方が状態同期が不要で単純。V1 はこちら）。
  - watcher コールバックは非 UI スレッド → `ExecuteScriptAsync` 呼び出しは既存の UI マーシャリング
    パターン（`Rhino.RhinoApp.InvokeOnUiThread` 相当、既存コードの流儀）に合わせる。
- `reloadSkills`: 対象タブ生存確認 → `Session.Write("/plugin reload-plugins\r")` →
  `SkillsLoadedAtUtc = DateTime.UtcNow` → `onSkillsList` 再送。
  claude が busy（プロンプト待ちでない）場合は入力がキューされるだけで壊れない（xterm 手打ちと同じ）。

## 6. UI 設計

### 6.1 ファイル構成

```
ui/src/
├── main.ts           # 変更: ⚡ボタン、レイアウト flex 化、skills-drawer の組み込み
└── skills-drawer.ts  # 新規: ドロワーの DOM 構築・描画・メッセージ処理（単一責務）
```

### 6.2 レイアウト変更

現在 `#terminals` が `flex: 1` で全幅。これを横並びコンテナで包む:

```
#terminal (column flex)
├── #tabbar（既存 + 右端に #skillsbtn。margin-left: auto で右寄せ）
└── #main (row flex)   ← 新規
    ├── #terminals（flex: 1、既存のまま）
    └── #skillsdrawer（flex: 0 0 260px、hidden 時 display: none）
```

- 開閉時に **アクティブタブの `safeFit()`** を呼ぶ（既存のリサイズ経路をそのまま使う。
  アニメーションは付けない — transition 中の fit 連打を避ける）。
- スタイルは既存の JS 注入 CSS に追記。配色はタブバーと同系（`#1a1a1a` 地・`#2a2a2a` 罫線）。

### 6.3 状態管理

| 状態 | 管理 | 理由 |
|------|------|------|
| ドロワー開閉 | `skills-drawer.ts` 内のローカル変数 + DOM class | 一時的 UI 状態。C# に伝えない（push は閉時に無視） |
| スキル一覧 | 最新の `onSkillsList` ペイロードをそのまま描画 | UI 側にキャッシュ・差分ロジックを持たない |
| 展開中の行 | 行要素の class | 再描画時は全折りたたみに戻ってよい（V1） |

### 6.4 エッジケース・空状態

| 状態 | 表示 |
|------|------|
| スキル 0 件 | ヒント文（2.2 の空状態） |
| description 空 | 「(説明なし)」 |
| name 重複（自作 vs 同梱） | そのまま両方表示（バッジで区別できる。claude 側の優先順位には関与しない） |
| タブが全滅 → 再 spawn | `onTabsReset` 後に `listSkills` を送り直す |

---

## 7. 選択理由（決定ログ）

| 決定 | 選択理由 |
|------|---------|
| 右サイドドロワー | ターミナルと同時に見えるので「一覧を見ながら依頼」できる（オーバーレイ/専用タブは会話と排他になる） |
| reload は専用メッセージ（stdin 直叩きにしない） | C# が `SkillsLoadedAtUtc` を進めないと ⚠ が消えない。副作用を C# に集約 |
| needsReload は mtime × タブ spawn 時刻 | claude の内部状態は外から読めない。誤検知しても「⟳ を押せば必ず正しい状態になる」方向に倒れる安全な近似 |
| watcher push は常時・UI 側で無視 | drawer 開閉状態を C# と同期する状態機械を作らない。push 1 本の方が壊れにくい |
| YAML ライブラリを入れない | 読むのは name/description の単一行値のみ。依存追加と .rhp サイズ増に見合わない |
| ui を skills-drawer.ts に分離 | main.ts は既に 359 行。ドロワーは xterm と独立した責務 |

---

## 8. ドキュメント追従（実装時に必須）

- `docs/architecture.md` — §2（コンポーネント表に SkillsCatalog / skills-drawer.ts）、§3（メッセージ表に 3 種追加）、§8（変更なし）
- `README.md` — 仕組みの箇条書きにドロワーを 1 行
- `docs/design-history.md` — 実装完了時に D9 として経緯を記録
- `docs/roadmap.md` — 本項目を消化済みに
