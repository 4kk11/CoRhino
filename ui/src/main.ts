import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { createSkillsDrawer, type SkillsListPayload } from './skills-drawer'
import { createSessionsDrawer, statusGlyph, type AgentStatus, type SessionsListPayload } from './sessions-drawer'
import { createSettingsDrawer, type PanelSettingsPayload, type SettingKey } from './settings-drawer'
import { L } from './i18n'

// The CLI a tab runs — the wire enum shared with the host (createTab /
// onTabCreated / onCliAvailability).
type CliKind = 'claude' | 'codex'

// The host created an agent (tab). sessionId is the session it runs (null for
// a codex tab until the host discovers the id and pushes onTabSession);
// label is the initial tab text (custom name > auto title > agentId) —
// delivered with creation so a resumed tab never flashes "agent-N" first.
// customName is the user-given name only (null when the label is an auto title
// or the agentId) — the rename input must prefill with THIS, never the label,
// or a blur-commit would freeze an auto title into a permanent custom name.
// background: an orchestrated child tab — create it without stealing focus.
type TabCreatedPayload = { agentId: string; sessionId: string | null; label: string; customName: string | null; cli: CliKind; background?: boolean }
type TabLabelPayload = { agentId: string; label: string; customName: string | null }
// An agent's activity state changed (from its status file, via the host's
// watcher). sessionId rides along so the sessions drawer's matching live row
// updates without a rescan (null while a codex id is undiscovered).
type TabStatusPayload = { agentId: string; sessionId: string | null; status: AgentStatus }
// A codex tab's session id was discovered after spawn.
type TabSessionPayload = { agentId: string; sessionId: string }
// Which CLIs resolve on PATH (probed once per panel boot; missing ones grey
// out in the + menu).
type CliAvailabilityPayload = { claude: boolean; codex: boolean }
// A tab's CLI didn't resolve — drive the in-pane setup view. 'missing' shows
// the full overlay, 'installing' clears it (installer output streams in the
// xterm), 'failed' shows a bottom banner so that output stays readable.
// command is the official native-installer one-liner (display + copy; the
// install button makes the host run exactly this string).
type CliSetupPayload = { agentId: string; phase: 'missing' | 'installing' | 'failed'; cli: CliKind; command: string }

declare global {
  interface Window {
    eto?: { postMessage: (msg: string) => void }
    CoRhinoPanel?: {
      // The host created an agent (tab); build an xterm for it and focus it.
      onTabCreated: (payload: TabCreatedPayload) => void
      // Output from an agent's PTY → its terminal.
      onPtyData: (agentId: string, text: string) => void
      // A fresh claude is starting in an existing tab; wipe its screen.
      onSessionRestart: (agentId: string) => void
      // An agent exited / was closed; drop its tab.
      onTabClosed: (agentId: string) => void
      // Reopen-respawn: drop all tabs before the host re-announces a fresh one.
      onTabsReset: () => void
      // Skill list for one tab (listSkills/reloadSkills response or watcher push).
      onSkillsList: (payload: SkillsListPayload) => void
      // Sessions list (listSessions response or post-change push), tab-independent.
      onSessionsList: (payload: SessionsListPayload) => void
      // A tab's label changed (rename, or an auto title appeared in a scan).
      onTabLabel: (payload: TabLabelPayload) => void
      // An agent's activity state changed (busy / waiting / idle).
      onTabStatus: (payload: TabStatusPayload) => void
      // A codex tab's session id landed (rename becomes possible).
      onTabSession: (payload: TabSessionPayload) => void
      // PATH probe result for the + menu.
      onCliAvailability: (payload: CliAvailabilityPayload) => void
      // A tab's CLI setup state changed (missing / installing / failed).
      onCliSetup: (payload: CliSetupPayload) => void
      // Panel settings (getSettings response / setSetting echo).
      onPanelSettings: (payload: PanelSettingsPayload) => void
    }
    chrome?: { webview?: { postMessage: (msg: string) => void } }
  }
}

type Outbound =
  | { type: 'ready' }
  | { type: 'createTab'; cli: CliKind }
  | { type: 'closeTab'; agentId: string }
  | { type: 'stdin'; agentId: string; data: string }
  | { type: 'resize'; agentId: string; cols: number; rows: number }
  | { type: 'listSkills'; agentId: string }
  | { type: 'openSkill'; path: string }
  | { type: 'reloadSkills'; agentId: string }
  | { type: 'listSessions' }
  | { type: 'resumeSession'; sessionId: string }
  | { type: 'renameSession'; sessionId: string; name: string }
  | { type: 'deleteSession'; sessionId: string }
  | { type: 'installCli'; agentId: string }
  | { type: 'recheckCli'; agentId: string }
  | { type: 'openShellInTab'; agentId: string }
  | { type: 'getSettings' }
  | { type: 'setSetting'; key: SettingKey; enabled: boolean }
  | { type: 'debug'; message: string }

function post(msg: Outbound) {
  const s = JSON.stringify(msg)
  try {
    // macOS WKWebView via Eto bridge
    if (window.eto?.postMessage) {
      window.eto.postMessage(s)
      return
    }
    // Windows WebView2 native
    if (window.chrome?.webview?.postMessage) {
      window.chrome.webview.postMessage(s)
      return
    }
    // Legacy / fallback (older WebBrowser controls)
    const ext = (window as any).external
    if (ext && typeof ext.notify === 'function') {
      ext.notify(s)
      return
    }
    console.error('[CoRhino] no postMessage bridge available')
  } catch (e) {
    console.error('[CoRhino] post failed', e)
  }
}

function debug(message: string) {
  post({ type: 'debug', message })
}

window.addEventListener('error', (ev) => {
  // Chromium-only benign notice fired when a ResizeObserver callback triggers a layout change in the same frame.
  if (ev.message?.includes('ResizeObserver loop')) return
  debug(`window.onerror: ${ev.message} @ ${ev.filename}:${ev.lineno}:${ev.colno} ${ev.error?.stack ?? ''}`)
})
window.addEventListener('unhandledrejection', (ev) => {
  debug(`unhandledrejection: ${ev.reason?.toString?.() ?? ev.reason}`)
})

debug(`main.ts entered. document.readyState=${document.readyState}`)

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => boot())
} else {
  boot()
}

function boot() {
  debug('boot() running')

  // Only WebView2 (Windows) needs the helper-textarea clamp below. WKWebView (macOS)
  // does not, and clamping the textarea to 0x0 there degrades WebKit's text-input
  // rect so fast / overlapping keystrokes get dropped ("rhino" → "rhn", xterm #5374).
  const isWindowsWebView2 = !!window.chrome?.webview?.postMessage

  const style = document.createElement('style')
  style.textContent = `
  html, body { margin: 0; padding: 0; height: 100%; width: 100%; background: #111; overflow: hidden; }
  #terminal { display: flex; flex-direction: column; height: 100%; width: 100%; }
  #tabbar {
    display: flex; align-items: stretch; height: 28px; flex: 0 0 28px;
    background: #1a1a1a; border-bottom: 1px solid #2a2a2a; overflow: hidden;
    font: 12px ui-monospace, Menlo, monospace; user-select: none;
  }
  /* Tabs scroll inside their own strip so #skillsbtn stays pinned at the right
     edge even when tabs overflow (an auto margin would scroll away with them). */
  #tabscroll {
    display: flex; align-items: stretch; flex: 1 1 auto;
    overflow-x: auto; overflow-y: hidden;
  }
  #tabscroll::-webkit-scrollbar { height: 0; }
  .tab {
    display: flex; align-items: center; gap: 6px; padding: 0 10px; height: 100%;
    color: #aaa; cursor: pointer; border-right: 1px solid #2a2a2a; white-space: nowrap;
  }
  .tab:hover { background: #222; }
  .tab.active { background: #111; color: #fff; }
  .tab .tablabel {
    max-width: 140px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  }
  .tab .tabrename {
    width: 140px; background: #111; color: #ddd; border: 1px solid #444;
    border-radius: 2px; padding: 1px 4px; outline: none;
    font: 12px ui-monospace, Menlo, monospace;
  }
  .tab .close { color: #777; padding: 0 2px; border-radius: 2px; }
  .tab .close:hover { color: #fff; background: #444; }
  #addtab {
    display: flex; align-items: center; justify-content: center; min-width: 28px; height: 100%;
    color: #aaa; cursor: pointer; border-right: 1px solid #2a2a2a; font-size: 16px; line-height: 1;
  }
  #addtab:hover { background: #222; color: #fff; }
  /* CLI picker for "+" — fixed-positioned so #tabbar's overflow doesn't clip it. */
  #climenu {
    position: fixed; z-index: 10; min-width: 150px;
    background: #1e1e1e; border: 1px solid #333; border-radius: 4px;
    box-shadow: 0 4px 12px rgba(0,0,0,.5); padding: 4px 0;
    font: 12px ui-monospace, Menlo, monospace; user-select: none;
  }
  .climenu-item {
    display: flex; align-items: center; gap: 8px; padding: 5px 12px;
    color: #ccc; cursor: pointer; white-space: nowrap;
  }
  .climenu-item:hover { background: #2a2a2a; color: #fff; }
  .climenu-item.disabled { opacity: 0.35; cursor: default; }
  .climenu-item.disabled:hover { background: transparent; color: #ccc; }
  .clibadge { font-size: 10px; }
  .clibadge.claude { color: #d0846a; }
  .clibadge.codex { color: #6f9fdf; }
  .tab .clibadge { flex: 0 0 auto; }
  /* Agent status glyph (tab bar + sessions drawer). A round dot read as a
     second CLI badge (● claude), so each state gets its own glyph instead:
     ✻ spinning amber = busy (claude's own spinner vocabulary), ‼ blinking
     red = waiting for approval (the state that most needs eyes), ✓ green =
     finished behind an inactive tab (unseen), and idle shows NOTHING — a
     quiet agent leaves a quiet tab. */
  .statusglyph { font-size: 10px; flex: 0 0 auto; line-height: 1; }
  .statusglyph.busy { color: #e0af68; animation: corhino-spin 1.6s linear infinite; }
  .statusglyph.waiting { color: #e05555; animation: corhino-blink 0.8s step-start infinite; }
  .statusglyph.unseen { color: #6ec26e; }
  .statusglyph.idle { display: none; }
  @keyframes corhino-spin { to { transform: rotate(360deg); } }
  @keyframes corhino-blink { 50% { opacity: 0.2; } }
  /* CLI setup view: full overlay (missing) or bottom banner (failed). */
  .clisetup {
    position: absolute; inset: 4px; z-index: 5;
    display: flex; align-items: center; justify-content: center;
    background: #111; color: #ccc;
    font: 12px ui-monospace, Menlo, monospace; user-select: none;
  }
  .clisetup.failed {
    top: auto; align-items: flex-end; justify-content: stretch;
    background: transparent; pointer-events: none;
  }
  .clisetup-box {
    display: flex; flex-direction: column; gap: 10px;
    max-width: 480px; min-width: 0; padding: 18px 22px;
  }
  .clisetup.failed .clisetup-box {
    pointer-events: auto; flex: 1 1 auto; max-width: none;
    padding: 10px 14px; background: #1c1616; border-top: 1px solid #5a3535;
  }
  .clisetup-title { display: flex; align-items: center; gap: 8px; font-size: 13px; color: #eee; }
  .clisetup-hint { color: #999; line-height: 1.5; }
  .clisetup-error { color: #e08585; line-height: 1.5; }
  .clisetup-cmd {
    display: flex; align-items: center; gap: 8px;
    background: #1a1a1a; border: 1px solid #2f2f2f; border-radius: 4px; padding: 6px 8px;
  }
  .clisetup-cmd code {
    flex: 1 1 auto; overflow-x: auto; white-space: nowrap; color: #d8d8d8;
    font: 11px ui-monospace, Menlo, monospace; user-select: text;
  }
  .clisetup-cmd code::-webkit-scrollbar { height: 0; }
  .clisetup button {
    background: #262626; color: #ccc; border: 1px solid #3a3a3a; border-radius: 4px;
    padding: 4px 12px; cursor: pointer; flex: 0 0 auto;
    font: 12px ui-monospace, Menlo, monospace;
  }
  .clisetup button:hover { background: #333; color: #fff; }
  .clisetup button.primary { background: #2d4a2d; border-color: #3f663f; color: #dfeede; }
  .clisetup button.primary:hover { background: #385938; }
  .clisetup-actions { display: flex; gap: 8px; }
  .clisetup-shell { color: #777; text-decoration: underline; cursor: pointer; align-self: flex-start; }
  .clisetup-shell:hover { color: #aaa; }
  #main { display: flex; flex-direction: row; flex: 1 1 auto; overflow: hidden; }
  #terminals { position: relative; flex: 1 1 auto; overflow: hidden; }
  .termpane { position: absolute; inset: 0; padding: 4px; box-sizing: border-box; }
  .termpane.hidden { display: none; }
  .xterm, .xterm-viewport { background: #111 !important; }
${isWindowsWebView2 ? `
  /* During Japanese IME composition xterm grows the helper textarea to fit the composing
     text (CompositionHelper.updateCompositionElements). On Chromium/WebView2 (unlike macOS
     WKWebView) that growth bubbles up as document overflow, scrolling the whole panel
     sideways and cascading into FitAddon → PTY resize → TUI redraw.
     We cap the textarea at 1x1 px to stop the growth WITHOUT zeroing it. xterm itself
     guarantees a 1x1 minimum (Math.max(bounds, 1)) because "certain IMEs may break" at 0x0.
     A 0x0 clamp hit exactly that break: the Windows IME re-emitted the first committed word
     on every later composition (日本語日本語日本語…) and never committed the new ones.
     1x1 keeps the layout stable and the IME working.
     macOS is excluded: there any clamp causes dropped keystrokes instead. */
  .xterm-helper-textarea { max-width: 1px; max-height: 1px; overflow: hidden !important; }
  /* IME anchor freeze (B13→B16→B17): xterm re-anchors the composition view and
     the helper textarea to the buffer cursor on EVERY render. The cursor is the
     right place at compositionstart (the user is typing in the input row), but
     while codex streams, its END-OF-FRAME cursor position genuinely moves
     (ratatui parks it wherever the paint ended; Ink/claude re-homes it to the
     input box) — so cursor-following makes the candidate window teleport even
     with atomic frames (B14). Fixed corner anchors (B13/B15) were wrong too:
     far from the real input row, covering other UI. The answer is to FREEZE the
     anchor per composition: compositionstart captures the cursor cell into
     --ime-x/y on the pane (same coordinate space xterm writes inline), and this
     rule outweighs xterm's per-render inline styles for the duration. */
  .termpane.composing .composition-view,
  .termpane.composing .xterm-helper-textarea {
    left: var(--ime-x) !important;
    top: var(--ime-y) !important;
  }
` : ''}
`
  document.head.appendChild(style)

  // Build the shell: a tab bar (with a trailing "+") above a stack of terminal panes.
  const rootEl = document.getElementById('terminal')!
  rootEl.innerHTML = ''
  const tabbar = document.createElement('div')
  tabbar.id = 'tabbar'
  const tabscroll = document.createElement('div')
  tabscroll.id = 'tabscroll'
  tabbar.appendChild(tabscroll)
  const addBtn = document.createElement('div')
  addBtn.id = 'addtab'
  addBtn.textContent = '+'
  addBtn.title = L.newAgent
  addBtn.addEventListener('click', (e) => {
    e.stopPropagation()
    toggleCliMenu()
  })
  tabscroll.appendChild(addBtn)

  // ---- "+" CLI picker ------------------------------------------------------
  // Small transient dropdown under the + button: pick which CLI the new tab
  // runs. Items grey out while their binary is missing from PATH (availability
  // arrives via onCliAvailability; claude starts enabled — it is the default
  // CLI and has a shell fallback — codex starts disabled until proven present).
  const CLI_META: { id: CliKind; name: string; badge: string }[] = [
    { id: 'claude', name: 'Claude Code', badge: '●' },
    { id: 'codex', name: 'Codex', badge: '◆' },
  ]
  let cliAvailability: CliAvailabilityPayload = { claude: true, codex: false }
  let cliMenu: HTMLElement | null = null

  function closeCliMenu() {
    cliMenu?.remove()
    cliMenu = null
  }

  function toggleCliMenu() {
    if (cliMenu) {
      closeCliMenu()
      return
    }
    const menu = document.createElement('div')
    menu.id = 'climenu'
    for (const meta of CLI_META) {
      const item = document.createElement('div')
      item.className = 'climenu-item'
      const badge = document.createElement('span')
      badge.className = `clibadge ${meta.id}`
      badge.textContent = meta.badge
      const name = document.createElement('span')
      name.textContent = meta.name
      item.appendChild(badge)
      item.appendChild(name)
      if (cliAvailability[meta.id]) {
        item.addEventListener('click', () => {
          closeCliMenu()
          post({ type: 'createTab', cli: meta.id })
        })
      } else {
        item.classList.add('disabled')
        item.title = L.cliUnavailable(meta.name)
      }
      menu.appendChild(item)
    }
    const rect = addBtn.getBoundingClientRect()
    menu.style.left = `${Math.round(rect.left)}px`
    menu.style.top = `${Math.round(rect.bottom + 2)}px`
    document.body.appendChild(menu)
    cliMenu = menu
  }

  // Any click outside (capture: before xterm handlers) or Esc dismisses it.
  // The + button is excluded — its own handler toggles, and this capture
  // listener runs first (closing here would make + reopen the menu).
  document.addEventListener('click', (e) => {
    const t = e.target as Node
    if (cliMenu && !cliMenu.contains(t) && !addBtn.contains(t)) closeCliMenu()
  }, { capture: true })
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && cliMenu) {
      e.stopPropagation()
      closeCliMenu()
    }
  }, { capture: true })
  // --------------------------------------------------------------------------
  const termsEl = document.createElement('div')
  termsEl.id = 'terminals'

  // Drawers: ⚡ (skills), 🕘 (sessions), and ⚙ (settings) at the tab bar's
  // right edge each toggle a fixed-width panel to the right of the terminals.
  // The drawer modules own their DOM; we own the layout slots, the message
  // bridge, exclusivity (opening one closes the others — there is only one
  // 260px slot's worth of room), and refitting the terminal on open/close.
  const drawer = createSkillsDrawer({
    requestList: (agentId) => post({ type: 'listSkills', agentId }),
    openSkill: (path) => post({ type: 'openSkill', path }),
    reloadSkills: (agentId) => post({ type: 'reloadSkills', agentId }),
    getActiveAgentId: () => activeTab?.agentId ?? null,
    getActiveCli: () => activeTab?.cli ?? null,
    onToggled: (open) => {
      if (open) {
        sessionsDrawer.close()
        settingsDrawer.close()
      }
      if (activeTab) safeFit(activeTab)
    },
  })
  const sessionsDrawer = createSessionsDrawer({
    requestList: () => post({ type: 'listSessions' }),
    resumeSession: (sessionId) => post({ type: 'resumeSession', sessionId }),
    renameSession: (sessionId, name) => post({ type: 'renameSession', sessionId, name }),
    deleteSession: (sessionId) => post({ type: 'deleteSession', sessionId }),
    onToggled: (open) => {
      if (open) {
        drawer.close()
        settingsDrawer.close()
      }
      if (activeTab) safeFit(activeTab)
    },
  })
  const settingsDrawer = createSettingsDrawer({
    requestSettings: () => post({ type: 'getSettings' }),
    setSetting: (key, enabled) => post({ type: 'setSetting', key, enabled }),
    onToggled: (open) => {
      if (open) {
        drawer.close()
        sessionsDrawer.close()
      }
      if (activeTab) safeFit(activeTab)
    },
  })
  tabbar.appendChild(sessionsDrawer.button)
  tabbar.appendChild(drawer.button)
  tabbar.appendChild(settingsDrawer.button)

  const mainEl = document.createElement('div')
  mainEl.id = 'main'
  mainEl.appendChild(termsEl)
  mainEl.appendChild(drawer.root)
  mainEl.appendChild(sessionsDrawer.root)
  mainEl.appendChild(settingsDrawer.root)

  rootEl.appendChild(tabbar)
  rootEl.appendChild(mainEl)

  type Tab = {
    agentId: string
    sessionId: string | null
    customName: string | null
    cli: CliKind
    term: Terminal
    fit: FitAddon
    pane: HTMLElement
    tabEl: HTMLElement
    labelEl: HTMLElement
    statusEl: HTMLElement
    // Last status pushed by the host (drives the tab dot).
    status: AgentStatus
    // The agent finished (busy/waiting → idle) while this tab was inactive;
    // the dot shows green until the user opens the tab.
    unseen: boolean
    lastCols: number
    lastRows: number
    // The CLI setup view covering (missing) or bannering (failed) this pane;
    // null while the tab runs a real PTY.
    setupEl: HTMLElement | null
    // Synchronized-update frame assembly state (see writeSynced).
    sync: { pending: string; active: boolean; timer: number | null }
  }

  const tabs = new Map<string, Tab>()
  let activeTab: Tab | null = null

  // macOS WebKit IME state shared across terminals — only the focused (active)
  // terminal's textarea ever composes, so a single flag suffices. Toggled by the
  // composition listeners attached to each terminal's textarea on creation.
  let composing = false

  function activeTextarea(): HTMLTextAreaElement | null {
    return activeTab?.term.textarea ?? null
  }

  function createTerminal(): { term: Terminal; fit: FitAddon } {
    const term = new Terminal({
      fontFamily: 'ui-monospace, Menlo, monospace',
      fontSize: 12,
      cursorBlink: true,
      // macOS PTY's line discipline converts \n to \r\n before output reaches us.
      // Windows ConPTY does not, so when claude.exe writes a bare \n the cursor
      // stays on the same column and subsequent text staircases off the right edge.
      // Enabling convertEol applies the \r\n synthesis in xterm regardless of host.
      convertEol: true,
      theme: { background: '#111111', foreground: '#dddddd' },
    })
    const fit = new FitAddon()
    term.loadAddon(fit)
    // xterm's built-in key handling has no modifier-aware Enter: Shift+Enter sends the
    // same \r as plain Enter, so it submits instead of inserting a line break — claude
    // and codex can't tell the two apart. Intercept it and feed \n through term.input()
    // (the same onData entry point the macOS keystroke workaround below uses) instead;
    // both CLIs already treat a bare LF (the byte Ctrl+J sends) as "insert newline", so
    // this reuses that existing behavior rather than inventing a new protocol.
    // preventDefault stops the browser's native textarea Enter-insertion (there's no
    // safety net for it on Windows the way swallowInput below covers macOS); returning
    // false stops xterm's own handler from also emitting \r for the same keydown.
    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== 'keydown' || e.key !== 'Enter' || !e.shiftKey) return true
      if (e.ctrlKey || e.metaKey || e.altKey || composing || e.isComposing) return true
      e.preventDefault()
      term.input('\n', true)
      return false
    })
    return { term, fit }
  }

  function activateTab(agentId: string) {
    const tab = tabs.get(agentId)
    if (!tab) return
    activeTab = tab
    for (const t of tabs.values()) {
      const on = t === tab
      t.pane.classList.toggle('hidden', !on)
      t.tabEl.classList.toggle('active', on)
    }
    // Opening the tab is what "reading" a completion means.
    if (tab.unseen) {
      tab.unseen = false
      renderTabStatus(tab)
    }
    // The pane just became visible; fit to its real size and focus it.
    safeFit(tab)
    tab.term.focus()
    // needsReload is per-tab (each spawned/reloaded at a different time), so the
    // drawer re-requests the list from the new active tab's perspective (and it
    // re-syncs its ⟳ visibility — codex has no in-session reload).
    drawer.notifyActiveTabChanged()
  }

  function addTab(payload: TabCreatedPayload) {
    const { agentId, sessionId, label, customName } = payload
    const cli: CliKind = payload.cli === 'codex' ? 'codex' : 'claude'
    if (tabs.has(agentId)) {
      activateTab(agentId)
      return
    }
    const pane = document.createElement('div')
    pane.className = 'termpane'
    termsEl.appendChild(pane)

    const { term, fit } = createTerminal()
    const { tabEl, labelEl, statusEl } = buildTabButton(agentId, label, cli)
    const tab: Tab = { agentId, sessionId, customName, cli, term, fit, pane, tabEl, labelEl, statusEl, status: 'idle', unseen: false, lastCols: term.cols, lastRows: term.rows, setupEl: null, sync: { pending: '', active: false, timer: null } }
    tabs.set(agentId, tab)
    tabscroll.insertBefore(tab.tabEl, addBtn)

    try {
      term.open(pane)
    } catch (e: any) {
      // Roll back everything allocated above so a failed open doesn't leave a
      // half-built tab (orphan map entry, DOM nodes, undisposed Terminal).
      debug(`term.open failed (${agentId}): ${e?.stack ?? e}`)
      term.dispose()
      pane.remove()
      tab.tabEl.remove()
      tabs.delete(agentId)
      return
    }

    term.onData((data) => post({ type: 'stdin', agentId, data }))
    term.onResize(({ cols, rows }) => post({ type: 'resize', agentId, cols, rows }))

    if (!isWindowsWebView2) {
      const ta = term.textarea
      ta?.addEventListener('compositionstart', () => { composing = true })
      ta?.addEventListener('compositionend', () => { composing = false })
    } else {
      // B17: freeze the IME anchor for each composition (see the .composing CSS
      // above). The capture happens at compositionstart — the one moment the
      // cursor is reliably at the input row. Cell metrics come from xterm's
      // private render service (same values updateCompositionElements uses);
      // if that internal shape ever changes, skip the freeze and fall back to
      // xterm's cursor-following.
      const ta = term.textarea
      ta?.addEventListener('compositionstart', () => {
        const cell = (term as any)._core?._renderService?.dimensions?.css?.cell
        if (!cell?.width || !cell?.height) return
        const buf = term.buffer.active
        const col = Math.min(buf.cursorX, term.cols - 1)
        pane.style.setProperty('--ime-x', `${col * cell.width}px`)
        pane.style.setProperty('--ime-y', `${buf.cursorY * cell.height}px`)
        pane.classList.add('composing')
      })
      ta?.addEventListener('compositionend', () => pane.classList.remove('composing'))
    }

    resizeObserver.observe(pane)
    if (payload.background && activeTab) {
      // Orchestrated child: keep the user where they are. The pane starts
      // hidden (activateTab is what un-hides panes; this one never ran for
      // it) and gets its real fit when the user first clicks the tab —
      // until then the PTY runs at the spawn default, which the TUI
      // redraws from on the later resize.
      pane.classList.add('hidden')
    } else {
      // User-initiated tab steals focus (matches clicking "+" to start
      // working in it). Also the fallback when no tab is active yet.
      activateTab(agentId)
    }
    // Report the initial geometry so the PTY sizes correctly from the first paint.
    post({ type: 'resize', agentId, cols: term.cols, rows: term.rows })
    debug(`tab ${agentId} created ${term.cols}x${term.rows}`)
  }

  function buildTabButton(agentId: string, initialLabel: string, cli: CliKind): { tabEl: HTMLElement; labelEl: HTMLElement; statusEl: HTMLElement } {
    const el = document.createElement('div')
    el.className = 'tab'
    // CLI badge (matches the + menu): ● claude / ◆ codex.
    const badge = document.createElement('span')
    badge.className = `clibadge ${cli}`
    badge.textContent = cli === 'codex' ? '◆' : '●'
    badge.title = cli === 'codex' ? 'Codex' : 'Claude Code'
    el.appendChild(badge)
    // Agent activity glyph (busy / waiting / unseen; hidden while idle),
    // driven by onTabStatus.
    const status = document.createElement('span')
    status.className = 'statusglyph idle'
    el.appendChild(status)
    const label = document.createElement('span')
    label.className = 'tablabel'
    label.textContent = initialLabel
    label.title = L.renameTabHint
    label.addEventListener('dblclick', (e) => {
      e.stopPropagation()
      const tab = tabs.get(agentId)
      // sessionId is the rename's persistence key; without one there is
      // nothing to attach a name to (defensive — the host always sends one).
      if (tab?.sessionId) startTabRename(tab)
    })
    const close = document.createElement('span')
    close.className = 'close'
    close.textContent = '×'
    close.title = L.closeAgent
    close.addEventListener('click', (e) => {
      e.stopPropagation()
      post({ type: 'closeTab', agentId })
    })
    el.appendChild(label)
    el.appendChild(close)
    el.addEventListener('click', () => activateTab(agentId))
    return { tabEl: el, labelEl: label, statusEl: status }
  }

  function renderTabStatus(tab: Tab) {
    // unseen only ever rides on idle (it is set on the busy/waiting → idle
    // transition and cleared the moment the status leaves idle).
    const state = tab.unseen ? 'unseen' : tab.status
    tab.statusEl.className = `statusglyph ${state}`
    tab.statusEl.textContent = statusGlyph(state)
    tab.statusEl.title =
      state === 'unseen' ? L.statusUnseen
      : state === 'busy' ? L.statusBusy
      : state === 'waiting' ? L.statusWaiting
      : L.statusIdle
  }

  // Swap the tab label for an inline input. Enter = commit, Esc = cancel,
  // blur = commit; an empty commit reverts to the auto title. The host persists
  // the name and pushes onTabLabel back, which is what updates the label — no
  // optimistic render, so the label always reflects what was actually saved.
  // (The document-level capture keydown hook ignores this input: it only acts
  // when the active terminal's textarea is focused.)
  function startTabRename(tab: Tab) {
    if (!tab.sessionId) return
    const sessionId = tab.sessionId
    const input = document.createElement('input')
    input.className = 'tabrename'
    // Prefill with the user-given name only; the auto title stays a placeholder
    // so a stray blur commits "" (= keep auto) instead of freezing the title.
    input.value = tab.customName ?? ''
    input.placeholder = tab.labelEl.textContent ?? ''
    input.addEventListener('click', (e) => e.stopPropagation())
    input.addEventListener('dblclick', (e) => e.stopPropagation())
    let done = false
    const finish = (commit: boolean) => {
      if (done) return
      done = true
      if (commit) post({ type: 'renameSession', sessionId, name: input.value.trim() })
      input.replaceWith(tab.labelEl)
      tab.term.focus()
    }
    input.addEventListener('keydown', (e) => {
      e.stopPropagation()
      if (e.key === 'Enter') finish(true)
      else if (e.key === 'Escape') finish(false)
    })
    input.addEventListener('blur', () => finish(true))
    tab.labelEl.replaceWith(input)
    input.focus()
    input.select()
  }

  function removeTab(agentId: string) {
    const tab = tabs.get(agentId)
    if (!tab) return
    if (tab.sync.timer != null) window.clearTimeout(tab.sync.timer)
    resizeObserver.unobserve(tab.pane)
    tab.term.dispose()
    tab.pane.remove()
    tab.tabEl.remove()
    tabs.delete(agentId)
    if (activeTab === tab) {
      activeTab = null
      const next = tabs.keys().next()
      if (!next.done) activateTab(next.value)
    }
  }

  // ---- Synchronized-update frame assembly ----------------------------------
  // TUIs (codex/ratatui) and modern ConPTY wrap each repaint in DEC private
  // mode 2026 (CSI ? 2026 h … l, "synchronized updates"): apply the whole
  // frame atomically or not at all. xterm.js 5.5 does NOT implement the mode,
  // so mid-frame states (lines cleared but not yet redrawn) render whenever a
  // frame spans multiple onPtyData chunks — under ConPTY's chatty repaint
  // stream that reads as violent flicker (design-history B13/B14). We assemble
  // frames here instead: buffer from 2026h until the matching 2026l, then hand
  // xterm the complete frame in ONE write. The markers themselves pass through
  // (xterm ignores unknown modes). A timeout backstops a frame whose close
  // marker never arrives (app died mid-frame, marker mangled) so output can
  // never wedge; it also drains a held marker-prefix tail on a stream that
  // pauses mid-escape.
  const SYNC_BEGIN = '\x1b[?2026h'
  const SYNC_END = '\x1b[?2026l'
  const SYNC_FLUSH_MS = 80

  function writeSynced(tab: Tab, text: string) {
    const s = tab.sync
    s.pending += text
    for (;;) {
      if (s.active) {
        const end = s.pending.indexOf(SYNC_END)
        if (end === -1) break // frame still open — keep buffering
        const cut = end + SYNC_END.length
        tab.term.write(s.pending.slice(0, cut))
        s.pending = s.pending.slice(cut)
        s.active = false
      } else {
        const begin = s.pending.indexOf(SYNC_BEGIN)
        if (begin === -1) {
          // Flush everything except a tail that could be a split marker.
          const keep = partialMarkerTail(s.pending)
          if (s.pending.length > keep) {
            tab.term.write(keep === 0 ? s.pending : s.pending.slice(0, -keep))
            s.pending = keep === 0 ? '' : s.pending.slice(-keep)
          }
          break
        }
        if (begin > 0) tab.term.write(s.pending.slice(0, begin))
        s.pending = s.pending.slice(begin)
        s.active = true
      }
    }
    if (s.pending.length > 0) {
      if (s.timer == null) s.timer = window.setTimeout(() => flushSync(tab), SYNC_FLUSH_MS)
    } else if (s.timer != null) {
      window.clearTimeout(s.timer)
      s.timer = null
    }
  }

  // Length of the longest suffix of `text` that is a proper prefix of the
  // sync markers ('\x1b[?2026' covers both h and l endings).
  function partialMarkerTail(text: string): number {
    const max = Math.min(SYNC_BEGIN.length - 1, text.length)
    for (let k = max; k > 0; k--) {
      if (text.endsWith(SYNC_BEGIN.slice(0, k))) return k
    }
    return 0
  }

  function flushSync(tab: Tab) {
    const s = tab.sync
    s.timer = null
    if (s.pending) {
      tab.term.write(s.pending)
      s.pending = ''
    }
    s.active = false
  }

  // ---- CLI setup view (missing-CLI onboarding) -----------------------------
  // Rendered into the tab's pane when its CLI didn't resolve: 'missing' is a
  // full centered overlay, 'failed' a bottom banner that leaves the installer
  // output readable. Cleared when a real PTY starts (onSessionRestart) or the
  // installer takes over the terminal ('installing').

  function clearCliSetup(tab: Tab, resendSize: boolean) {
    tab.setupEl?.remove()
    tab.setupEl = null
    // The host swaps in a fresh PTY on every relaunch (PtySession is one-shot),
    // which boots on the 80x24 default — re-report the pane's real geometry.
    // Unconditionally: the overlay may already be gone (install flow cleared it
    // at 'installing'), but the relaunch after the installer still needs a size.
    if (resendSize) post({ type: 'resize', agentId: tab.agentId, cols: tab.term.cols, rows: tab.term.rows })
  }

  function renderCliSetup(tab: Tab, payload: CliSetupPayload) {
    clearCliSetup(tab, false)
    const failed = payload.phase === 'failed'
    const displayName = payload.cli === 'codex' ? 'Codex' : 'Claude Code'

    const box = document.createElement('div')
    box.className = 'clisetup-box'

    if (failed) {
      const err = document.createElement('div')
      err.className = 'clisetup-error'
      err.textContent = L.installStillMissing
      box.appendChild(err)
    } else {
      const title = document.createElement('div')
      title.className = 'clisetup-title'
      const badge = document.createElement('span')
      badge.className = `clibadge ${payload.cli}`
      badge.textContent = payload.cli === 'codex' ? '◆' : '●'
      title.appendChild(badge)
      title.appendChild(document.createTextNode(L.cliMissingTitle(displayName)))
      box.appendChild(title)
      const hint = document.createElement('div')
      hint.className = 'clisetup-hint'
      hint.textContent = L.cliMissingHint(payload.cli)
      box.appendChild(hint)
    }

    const cmdRow = document.createElement('div')
    cmdRow.className = 'clisetup-cmd'
    const code = document.createElement('code')
    code.textContent = payload.command
    cmdRow.appendChild(code)
    const copyBtn = document.createElement('button')
    copyBtn.textContent = L.installCopy
    copyBtn.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(payload.command)
      } catch {
        // WKWebView can deny the async clipboard outside a "secure" context;
        // the legacy execCommand path still works from a click handler.
        const ta = document.createElement('textarea')
        ta.value = payload.command
        document.body.appendChild(ta)
        ta.select()
        document.execCommand('copy')
        ta.remove()
      }
      copyBtn.textContent = L.installCopied
      window.setTimeout(() => { copyBtn.textContent = L.installCopy }, 1500)
    })
    cmdRow.appendChild(copyBtn)
    box.appendChild(cmdRow)

    const actions = document.createElement('div')
    actions.className = 'clisetup-actions'
    const installBtn = document.createElement('button')
    installBtn.className = 'primary'
    installBtn.textContent = L.installRun
    installBtn.addEventListener('click', () => post({ type: 'installCli', agentId: tab.agentId }))
    const recheckBtn = document.createElement('button')
    recheckBtn.textContent = L.installRecheck
    recheckBtn.addEventListener('click', () => post({ type: 'recheckCli', agentId: tab.agentId }))
    actions.appendChild(installBtn)
    actions.appendChild(recheckBtn)
    box.appendChild(actions)

    if (!failed) {
      const shell = document.createElement('a')
      shell.className = 'clisetup-shell'
      shell.textContent = L.openShellInstead
      shell.addEventListener('click', () => post({ type: 'openShellInTab', agentId: tab.agentId }))
      box.appendChild(shell)
    }

    const overlay = document.createElement('div')
    overlay.className = failed ? 'clisetup failed' : 'clisetup'
    overlay.appendChild(box)
    tab.pane.appendChild(overlay)
    tab.setupEl = overlay
  }

  // Debounced fit for the active pane. Without debouncing, a single resize can chain
  // into a feedback loop: claude paints → scrollbar appears → ResizeObserver fires →
  // fit() shrinks cols → PTY resize → claude repaints → loop. The guard on (cols,rows)
  // change avoids posting redundant resize events when only sub-cell pixels shifted.
  function safeFit(tab: Tab) {
    try {
      tab.fit.fit()
      if (tab.term.cols !== tab.lastCols || tab.term.rows !== tab.lastRows) {
        tab.lastCols = tab.term.cols
        tab.lastRows = tab.term.rows
      }
    } catch {}
  }
  let resizeTimer: number | undefined
  const resizeObserver = new ResizeObserver(() => {
    if (resizeTimer !== undefined) window.clearTimeout(resizeTimer)
    resizeTimer = window.setTimeout(() => { if (activeTab) safeFit(activeTab) }, 80)
  })

  // On macOS/WKWebView, xterm reads typed characters through the helper textarea's
  // input/composition path, which WebKit drops under fast/overlapping keystrokes
  // ("rhino" → "rhn"; xterm #5374, area/ime — open upstream, no fix). keydown is
  // delivered earlier than that path, so a capture-phase keydown listener (running
  // before xterm's textarea listener) forwards printable keys via xterm's public
  // input() — keeping onData as the single stdin path — and stops the event so the
  // lossy textarea route never sees it. IME / control keys fall through to xterm so
  // Japanese input still works. The listeners target whichever terminal is active.
  // Windows WebView2 keeps the default path.
  if (!isWindowsWebView2) {
    const isPlainPrintable = (e: KeyboardEvent) =>
      e.type === 'keydown' &&
      !composing &&
      !e.isComposing &&
      !e.ctrlKey &&
      !e.metaKey &&
      !e.altKey &&
      e.key.length === 1

    document.addEventListener('keydown', (e) => {
      const ta = activeTextarea()
      if (!ta || document.activeElement !== ta) return
      if (!isPlainPrintable(e)) return
      e.preventDefault()
      e.stopPropagation()
      activeTab!.term.input(e.key, true)
    }, { capture: true })

    // preventDefault on a keyCode-229 keydown does NOT stop WebKit from also
    // inserting the character through the input event, which xterm would then send
    // a second time (→ "rrhhiinnoo"). Swallow the non-composing input event before
    // xterm's own textarea listener sees it (document-level capture runs first), so
    // IME composition input is left alone and Japanese still works.
    const swallowInput = (e: Event) => {
      const ta = activeTextarea()
      if (!ta || e.target !== ta) return
      if (composing || (e as InputEvent).isComposing) return
      e.stopImmediatePropagation()
      ta.value = ''
    }
    document.addEventListener('beforeinput', swallowInput, { capture: true })
    document.addEventListener('input', swallowInput, { capture: true })
  }

  window.CoRhinoPanel = {
    onTabCreated(payload: TabCreatedPayload) {
      addTab(payload)
    },
    onPtyData(agentId: string, text: string) {
      const tab = tabs.get(agentId)
      if (tab) writeSynced(tab, text)
    },
    onSessionRestart(agentId: string) {
      const tab = tabs.get(agentId)
      if (!tab) return
      clearCliSetup(tab, true)
      // Drop any half-assembled frame from the dead session — it must not
      // replay into the fresh one.
      if (tab.sync.timer != null) { window.clearTimeout(tab.sync.timer); tab.sync.timer = null }
      tab.sync.pending = ''
      tab.sync.active = false
      tab.term.reset()
    },
    onTabClosed(agentId: string) {
      removeTab(agentId)
    },
    onTabsReset() {
      for (const id of [...tabs.keys()]) removeTab(id)
    },
    onSkillsList(payload: SkillsListPayload) {
      drawer.handleSkillsList(payload)
    },
    onSessionsList(payload: SessionsListPayload) {
      sessionsDrawer.handleSessionsList(payload)
    },
    onTabLabel(payload: TabLabelPayload) {
      const tab = tabs.get(payload.agentId)
      if (!tab) return
      tab.customName = payload.customName
      tab.labelEl.textContent = payload.label
    },
    onTabStatus(payload: TabStatusPayload) {
      const tab = tabs.get(payload.agentId)
      if (!tab) return
      const prev = tab.status
      tab.status = payload.status
      // "Completed while you weren't looking": a busy/waiting agent going idle
      // behind an inactive tab keeps a green dot until the tab is opened. The
      // active tab's completions are considered seen as they happen.
      if (payload.status === 'idle' && prev !== 'idle' && tab !== activeTab) {
        tab.unseen = true
      } else if (payload.status !== 'idle') {
        tab.unseen = false
      }
      renderTabStatus(tab)
      // Mirror onto the sessions drawer's live row (no rescan needed).
      if (payload.sessionId) sessionsDrawer.updateStatus(payload.sessionId, payload.status)
    },
    onTabSession(payload: TabSessionPayload) {
      const tab = tabs.get(payload.agentId)
      if (!tab) return
      tab.sessionId = payload.sessionId
    },
    onCliAvailability(payload: CliAvailabilityPayload) {
      cliAvailability = payload
      // If the menu is open, rebuild it with the fresh availability.
      if (cliMenu) {
        closeCliMenu()
        toggleCliMenu()
      }
    },
    onCliSetup(payload: CliSetupPayload) {
      const tab = tabs.get(payload.agentId)
      if (!tab) return
      // 'installing': the installer owns the terminal now — just clear the
      // overlay (and re-send the size for the fresh PTY underneath).
      if (payload.phase === 'installing') {
        clearCliSetup(tab, true)
        return
      }
      renderCliSetup(tab, payload)
    },
    onPanelSettings(payload: PanelSettingsPayload) {
      settingsDrawer.handlePanelSettings(payload)
    },
  }

  post({ type: 'ready' })
  debug('xterm.js shell initialized (awaiting host tabs)')
}
