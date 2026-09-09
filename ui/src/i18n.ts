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
  // Settings Drawer
  settingsBtn: '設定',
  settingsTitle: '設定',
  sandboxTitle: 'サンドボックス',
  sandboxDesc: 'エージェントのコマンド実行を OS レベルで隔離し、ファイルの書き込み先を CoRhino ワークスペース内に制限します。範囲外の操作は通常の承認フローに戻ります。Rhino モデルの操作（MCP 経由）には影響しません。対象は Claude Code のタブのみ（Codex は常時サンドボックスで動作）。',
  sandboxAppliesToNewTabs: '変更は新しいタブから適用されます（実行中のセッションには影響しません）',
  sandboxUnsupported: 'この OS では利用できません（macOS のみ対応）',
  codexNetworkTitle: 'Codex のネットワーク',
  codexNetworkDesc: 'Codex タブのコマンド実行にネットワークを許可します。この 1 つのフラグに外部通信・ローカルの Unix ソケット・macOS キーチェーンへの到達がまとめて紐づいており、OFF（既定）だとキーチェーンに保存済みの認証情報も「無い」ものとして扱われます。1Password CLI（op）はどちらでも動きません（アプリとの XPC 通信がサンドボックスの許可対象外のため）——キーチェーンにキャッシュ済みの鍵を使うツールは ON で動きます。対象は Codex のタブのみ（Claude Code には影響しません）。',
  codexNetworkOn: 'ON: Codex のコマンド実行が外部ネットワークへ自由に接続できます',
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
  settingsBtn: 'Settings',
  settingsTitle: 'Settings',
  sandboxTitle: 'Sandbox',
  sandboxDesc: "Isolates the agent's command execution at the OS level, confining file writes to the CoRhino workspace. Anything outside falls back to the normal approval flow. Rhino model operations (via MCP) are unaffected. Applies to Claude Code tabs only (Codex always runs sandboxed).",
  sandboxAppliesToNewTabs: 'Applies to new tabs (running sessions are unaffected)',
  sandboxUnsupported: 'Not available on this OS (macOS only)',
  codexNetworkTitle: 'Codex network',
  codexNetworkDesc: "Allows network access from commands run in Codex tabs. This one flag also gates local unix sockets and the macOS Keychain, so with it OFF a keychain-cached credential reads as absent. The 1Password CLI (op) does not work either way — its XPC channel to the desktop app is outside the sandbox's allowed services — but tools reading a key already cached in the keychain do work when this is ON. Applies to Codex tabs only (Claude Code is unaffected).",
  codexNetworkOn: 'ON: commands in Codex tabs can reach the network freely',
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
