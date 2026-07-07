# Skills Drawer タスクリスト

| 項目 | 内容 |
|------|------|
| 対象 | パネル右サイドのスキル一覧ドロワー |
| 設計書 | [`skills-drawer-design.md`](skills-drawer-design.md) |
| 作成日 | 2026-07-07 |
| テスト方針 | リポジトリにテスト基盤が無いため、ビルド（0 警告）+ 実機確認を各フェーズのゲートにする。frontmatter パーサのみ関数を純粋に保ち将来のテスト導入に備える |
| レビュー方針 | 各 Phase 完了時に Codex レビュー → LGTM まで |
| 進捗管理 | 各 Phase 完了時にチェックボックスを [x] に更新 |

---

## Phase 1: C# データ層（SkillsCatalog）

- [x] **1-1**: `src/Panel/SkillsCatalog.cs` を新規作成
  - `SkillInfo`（name / description / source / path / needsReload）
  - `Scan(skillsLoadedAtUtc)`: user（`Workspace.UserSkillsRoot/skills/*/SKILL.md`）+ bundled（`<assembly dir>/cc-plugin/skills/*/SKILL.md`）を列挙
  - frontmatter パース（`---` ブロックの `name:` / `description:` 単一行値のみ。壊れたファイルはフォールバック + `Log.Write`）
  - `ToJson(agentId, skills)` / `IsKnownSkillPath(path)`
- [x] **1-2**: ビルド pass（0 警告）を確認
- [ ] **1-3**: 📋 Codex レビュー（⚠ codex CLI 認証切れで未消化。代替で汎用レビューエージェント1パス実施済み。`codex login` 後に再実行）
  - 対象: `SkillsCatalog.cs`
  - 観点: パス検証の抜け、frontmatter パースの境界（BOM・CRLF・`---` が本文に出るケース）

## Phase 2: C# パネル配線（メッセージ + watcher）

- [x] **2-1**: `AgentTab` に `SkillsLoadedAtUtc` を追加（spawn 時に UtcNow）
- [x] **2-2**: メッセージハンドラ 3 種を追加
  - `listSkills` → `onSkillsList(json)` を返す
  - `openSkill` → `IsKnownSkillPath` 検証 → `Process.Start`（UseShellExecute）
  - `reloadSkills` → タブ生存確認 → PTY へ `"/plugin reload-plugins\r"` → `SkillsLoadedAtUtc = now` → `onSkillsList` 再送
- [x] **2-3**: `FileSystemWatcher`（user skills を再帰監視、300ms デバウンス、UI スレッドへマーシャル、`Dispose` で破棄）
- [x] **2-4**: ビルド pass を確認
- [ ] **2-5**: 📋 Codex レビュー（⚠ 1-3 と同じく codex 認証切れで未消化）
  - 対象: `CoRhinoPanel.cs` 差分
  - 観点: watcher のスレッド境界・リーク、タブ破棄との競合（stale tab への注入）、D7 レビューで出た類のライフサイクルバグ

## Phase 3: UI（ドロワー）

- [x] **3-1**: `ui/src/skills-drawer.ts` を新規作成
  - DOM 構築（ヘッダー + ⟳ / スキル行 / 展開部 / 空状態）
  - `onSkillsList` の描画（drawer 閉時は無視）
  - 行クリック展開、「エディタで開く」→ `openSkill`、⟳ → `reloadSkills`
- [x] **3-2**: `main.ts` 統合
  - `#main`（row flex）導入、`#skillsbtn`（tabbar 右端・margin-left: auto）
  - 開閉時に `safeFit()`、タブ切替・`onTabsReset` 時に `listSkills` 再送
  - メッセージ型定義の追加
- [x] **3-3**: `dotnet build`（UI ビルド込み）pass、tsc strict 0 エラーを確認
- [ ] **3-4**: 📋 Codex レビュー（⚠ 1-3 と同じく codex 認証切れで未消化）
  - 対象: `skills-drawer.ts` / `main.ts` 差分
  - 観点: XSS（description は必ず textContent で挿入）、fit 連打、タブ切替との整合

## Phase 4: 実機確認 + ドキュメント追従

- [ ] **4-1**: 実機確認（Rhino 8 / macOS）
  - ⚡ でドロワー開閉 → ターミナルが正しくリサイズされる
  - 自作 + 同梱スキルが出所バッジ付きで並ぶ
  - save-rhino-skill で保存 → ライブで一覧に現れ ⚠ が付く → ⟳ → ● に変わり、スキルが発動する
  - タブ切替で needsReload がタブ視点に切り替わる
  - 「エディタで開く」で SKILL.md が開く
- [ ] **4-2**: Windows 実機確認(可能なら)（openSkill / watcher / ConPTY への reload 注入）
- [x] **4-3**: ドキュメント追従
  - `docs/architecture.md` §2 / §3、`README.md`、`docs/design-history.md`（D9）、`docs/roadmap.md` 消化
- [ ] **4-4**: 📋 最終 Codex レビュー → コミット（ユーザー承認後）

---

## 実装順序

```
Phase 1（SkillsCatalog）
  ↓
Phase 2（パネル配線）← Phase 1 の SkillInfo/ToJson に依存
  ↓
Phase 3（UI）← Phase 2 のメッセージに依存（ただし 3-1 のDOM骨格は並行可）
  ↓
Phase 4（実機確認・docs・コミット）
```
