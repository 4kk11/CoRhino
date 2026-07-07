// Two-language (ja/en) UI strings, following RHINO's language setting: the C#
// host injects `window.CORHINO_LANG` ("ja" | "en", from Rhino's LanguageIdentifier)
// into the bundled HTML before any script runs, so the shell can build its DOM
// with the right strings synchronously at boot. The dev-server path
// (CORHINO_WEBUI_URL) has no injection point — fall back to the browser locale.

declare global {
  interface Window {
    CORHINO_LANG?: string
  }
}

const ja = {
  newAgent: '新しいエージェント',
  closeAgent: 'このエージェントを終了',
  renameTabHint: 'ダブルクリックで名前を変更',
  cliUnavailable: (name: string) => `${name} が PATH に見つかりません`,
  skillsUnsupported: 'この CLI ではスキル一覧は使えません',
  // Skills Drawer
  skillsBtn: 'スキル一覧',
  skillsReload: 'アクティブなセッションにスキルを再読み込み (/plugin reload-plugins)',
  skillsEmpty: 'まだスキルがありません。claude に「今の手順をスキルにして」と頼むと保存されます。',
  badgeUser: '自作',
  badgeBundled: '同梱',
  needsReload: '要 reload（⟳ で反映）',
  noDescription: '(説明なし)',
  openInEditor: '↗ エディタで開く',
  // Sessions Drawer
  sessionsBtn: '過去のセッション',
  sessionsEmpty: 'まだセッションがありません。タブで会話するとここに履歴が残ります。',
  renameSession: '名前を変更（空にすると自動タイトルに戻る）',
  deleteSession: 'このセッションの履歴を削除',
  deleteConfirm: '削除?',
  running: '実行中',
  clickToResume: 'クリックで再開',
  liveRowHint: 'このセッションはタブで実行中です',
  justNow: 'たった今',
  minutesAgo: (n: number) => `${n}分前`,
  hoursAgo: (n: number) => `${n}時間前`,
  daysAgo: (n: number) => `${n}日前`,
}

const en: typeof ja = {
  newAgent: 'New agent',
  closeAgent: 'Close this agent',
  renameTabHint: 'Double-click to rename',
  cliUnavailable: (name: string) => `${name} not found on PATH`,
  skillsUnsupported: 'Skills are not available for this CLI',
  skillsBtn: 'Skills',
  skillsReload: 'Reload skills into the active session (/plugin reload-plugins)',
  skillsEmpty: 'No skills yet. Ask claude to "turn this workflow into a skill" and it lands here.',
  badgeUser: 'yours',
  badgeBundled: 'bundled',
  needsReload: 'reload needed (press ⟳)',
  noDescription: '(no description)',
  openInEditor: '↗ Open in editor',
  sessionsBtn: 'Past sessions',
  sessionsEmpty: 'No sessions yet. Conversations in tabs will show up here.',
  renameSession: 'Rename (clear to restore the auto title)',
  deleteSession: "Delete this session's history",
  deleteConfirm: 'Delete?',
  running: 'running',
  clickToResume: 'Click to resume',
  liveRowHint: 'This session is open in a tab',
  justNow: 'just now',
  minutesAgo: (n: number) => `${n}m ago`,
  hoursAgo: (n: number) => `${n}h ago`,
  daysAgo: (n: number) => `${n}d ago`,
}

const lang = window.CORHINO_LANG ?? (navigator.language.startsWith('ja') ? 'ja' : 'en')

export const L = lang === 'ja' ? ja : en

export function relTime(iso: string): string {
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return ''
  const s = Math.max(0, (Date.now() - t) / 1000)
  if (s < 60) return L.justNow
  if (s < 3600) return L.minutesAgo(Math.floor(s / 60))
  if (s < 86400) return L.hoursAgo(Math.floor(s / 3600))
  return L.daysAgo(Math.floor(s / 86400))
}
