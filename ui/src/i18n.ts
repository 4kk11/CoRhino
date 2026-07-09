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
  // CLI setup view (missing-CLI onboarding)
  cliMissingTitle: (name: string) => `${name} が見つかりません`,
  cliMissingHint: (bin: string) => `PATH に \`${bin}\` がありません。公式のネイティブインストーラで導入できます:`,
  installRun: 'インストールを実行',
  installRecheck: '再チェック',
  installCopy: 'コピー',
  installCopied: 'コピーしました',
  installStillMissing: 'CLI がまだ見つかりません。出力を確認するか、手動でインストールして「再チェック」を押してください。',
  openShellInstead: 'かわりにシェルを開く',
  // Skills Drawer
  skillsBtn: 'スキル一覧',
  skillsReload: 'アクティブなセッションにスキルを再読み込み (/plugin reload-plugins)',
  skillsEmpty: 'まだスキルがありません。claude に「今の手順をスキルにして」と頼むと保存されます。',
  badgeUser: '自作',
  badgeBundled: '同梱',
  needsReload: '要 reload（⟳ で反映）',
  needsReloadNewTab: 'このセッションには未反映（新しいタブから有効）',
  noDescription: '(説明なし)',
  openInEditor: '↗ エディタで開く',
  // Sessions Drawer
  sessionsBtn: '過去のセッション',
  sessionsEmpty: 'まだセッションがありません。タブで会話するとここに履歴が残ります。',
  renameSession: '名前を変更（空にすると自動タイトルに戻る）',
  deleteSession: 'このセッションの履歴を削除',
  deleteConfirm: '削除?',
  running: '実行中',
  statusBusy: '稼働中',
  statusWaiting: '承認待ち',
  statusIdle: '待機中',
  statusUnseen: '完了（未読）',
  clickToResume: 'クリックで再開',
  liveRowHint: 'このセッションはタブで実行中です',
  justNow: 'たった今',
  minutesAgo: (n: number) => `${n}分前`,
  hoursAgo: (n: number) => `${n}時間前`,
  daysAgo: (n: number) => `${n}日前`,
  spawnBadgeTitle: 'エージェントが spawn した子セッション',
  spawnedFrom: (name: string) => `↳ ${name} から spawn`,
  spawnedOrphan: '↳ エージェント spawn（親セッションは削除済み）',
}

const en: typeof ja = {
  newAgent: 'New agent',
  closeAgent: 'Close this agent',
  renameTabHint: 'Double-click to rename',
  cliUnavailable: (name: string) => `${name} not found on PATH`,
  cliMissingTitle: (name: string) => `${name} not found`,
  cliMissingHint: (bin: string) => `\`${bin}\` is not on PATH. Install it with the official native installer:`,
  installRun: 'Install',
  installRecheck: 'Re-check',
  installCopy: 'Copy',
  installCopied: 'Copied',
  installStillMissing: 'Still not found. Check the output, or install manually and hit Re-check.',
  openShellInstead: 'Open a shell instead',
  skillsBtn: 'Skills',
  skillsReload: 'Reload skills into the active session (/plugin reload-plugins)',
  skillsEmpty: 'No skills yet. Ask claude to "turn this workflow into a skill" and it lands here.',
  badgeUser: 'yours',
  badgeBundled: 'bundled',
  needsReload: 'reload needed (press ⟳)',
  needsReloadNewTab: 'not in this session (new tabs pick it up)',
  noDescription: '(no description)',
  openInEditor: '↗ Open in editor',
  sessionsBtn: 'Past sessions',
  sessionsEmpty: 'No sessions yet. Conversations in tabs will show up here.',
  renameSession: 'Rename (clear to restore the auto title)',
  deleteSession: "Delete this session's history",
  deleteConfirm: 'Delete?',
  running: 'running',
  statusBusy: 'working',
  statusWaiting: 'needs approval',
  statusIdle: 'waiting for input',
  statusUnseen: 'done (unseen)',
  clickToResume: 'Click to resume',
  liveRowHint: 'This session is open in a tab',
  justNow: 'just now',
  minutesAgo: (n: number) => `${n}m ago`,
  hoursAgo: (n: number) => `${n}h ago`,
  daysAgo: (n: number) => `${n}d ago`,
  spawnBadgeTitle: 'Child session spawned by an agent',
  spawnedFrom: (name: string) => `↳ spawned from ${name}`,
  spawnedOrphan: '↳ agent-spawned (parent session deleted)',
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
