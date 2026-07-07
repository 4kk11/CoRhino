import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { createSkillsDrawer, type SkillsListPayload } from './skills-drawer'
import { createSessionsDrawer, type SessionsListPayload } from './sessions-drawer'

// The host created an agent (tab). sessionId is the claude session it runs;
// label is the initial tab text (custom name > auto title > agentId) —
// delivered with creation so a resumed tab never flashes "agent-N" first.
// customName is the user-given name only (null when the label is an auto title
// or the agentId) — the rename input must prefill with THIS, never the label,
// or a blur-commit would freeze an auto title into a permanent custom name.
type TabCreatedPayload = { agentId: string; sessionId: string | null; label: string; customName: string | null }
type TabLabelPayload = { agentId: string; label: string; customName: string | null }

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
    }
    chrome?: { webview?: { postMessage: (msg: string) => void } }
  }
}

type Outbound =
  | { type: 'ready' }
  | { type: 'createTab' }
  | { type: 'closeTab'; agentId: string }
  | { type: 'stdin'; agentId: string; data: string }
  | { type: 'resize'; agentId: string; cols: number; rows: number }
  | { type: 'listSkills'; agentId: string }
  | { type: 'openSkill'; path: string }
  | { type: 'reloadSkills'; agentId: string }
  | { type: 'listSessions' }
  | { type: 'resumeSession'; sessionId: string }
  | { type: 'renameSession'; sessionId: string; name: string }
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
  addBtn.title = '新しいエージェント'
  addBtn.addEventListener('click', () => post({ type: 'createTab' }))
  tabscroll.appendChild(addBtn)
  const termsEl = document.createElement('div')
  termsEl.id = 'terminals'

  // Drawers: ⚡ (skills) and 🕘 (sessions) at the tab bar's right edge each
  // toggle a fixed-width panel to the right of the terminals. The drawer modules
  // own their DOM; we own the layout slots, the message bridge, exclusivity
  // (opening one closes the other — there is only one 260px slot's worth of
  // room), and refitting the terminal on open/close.
  const drawer = createSkillsDrawer({
    requestList: (agentId) => post({ type: 'listSkills', agentId }),
    openSkill: (path) => post({ type: 'openSkill', path }),
    reloadSkills: (agentId) => post({ type: 'reloadSkills', agentId }),
    getActiveAgentId: () => activeTab?.agentId ?? null,
    onToggled: (open) => {
      if (open) sessionsDrawer.close()
      if (activeTab) safeFit(activeTab)
    },
  })
  const sessionsDrawer = createSessionsDrawer({
    requestList: () => post({ type: 'listSessions' }),
    resumeSession: (sessionId) => post({ type: 'resumeSession', sessionId }),
    renameSession: (sessionId, name) => post({ type: 'renameSession', sessionId, name }),
    onToggled: (open) => {
      if (open) drawer.close()
      if (activeTab) safeFit(activeTab)
    },
  })
  tabbar.appendChild(sessionsDrawer.button)
  tabbar.appendChild(drawer.button)

  const mainEl = document.createElement('div')
  mainEl.id = 'main'
  mainEl.appendChild(termsEl)
  mainEl.appendChild(drawer.root)
  mainEl.appendChild(sessionsDrawer.root)
  rootEl.appendChild(tabbar)
  rootEl.appendChild(mainEl)

  type Tab = {
    agentId: string
    sessionId: string | null
    customName: string | null
    term: Terminal
    fit: FitAddon
    pane: HTMLElement
    tabEl: HTMLElement
    labelEl: HTMLElement
    lastCols: number
    lastRows: number
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
    // The pane just became visible; fit to its real size and focus it.
    safeFit(tab)
    tab.term.focus()
    // needsReload is per-tab (each spawned/reloaded at a different time), so the
    // drawer re-requests the list from the new active tab's perspective.
    drawer.notifyActiveTabChanged()
  }

  function addTab(payload: TabCreatedPayload) {
    const { agentId, sessionId, label, customName } = payload
    if (tabs.has(agentId)) {
      activateTab(agentId)
      return
    }
    const pane = document.createElement('div')
    pane.className = 'termpane'
    termsEl.appendChild(pane)

    const { term, fit } = createTerminal()
    const { tabEl, labelEl } = buildTabButton(agentId, label)
    const tab: Tab = { agentId, sessionId, customName, term, fit, pane, tabEl, labelEl, lastCols: term.cols, lastRows: term.rows }
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
    }

    resizeObserver.observe(pane)
    // New tab steals focus (matches clicking "+" to start working in it).
    activateTab(agentId)
    // Report the initial geometry so the PTY sizes correctly from the first paint.
    post({ type: 'resize', agentId, cols: term.cols, rows: term.rows })
    debug(`tab ${agentId} created ${term.cols}x${term.rows}`)
  }

  function buildTabButton(agentId: string, initialLabel: string): { tabEl: HTMLElement; labelEl: HTMLElement } {
    const el = document.createElement('div')
    el.className = 'tab'
    const label = document.createElement('span')
    label.className = 'tablabel'
    label.textContent = initialLabel
    label.title = 'ダブルクリックで名前を変更'
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
    close.title = 'このエージェントを終了'
    close.addEventListener('click', (e) => {
      e.stopPropagation()
      post({ type: 'closeTab', agentId })
    })
    el.appendChild(label)
    el.appendChild(close)
    el.addEventListener('click', () => activateTab(agentId))
    return { tabEl: el, labelEl: label }
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
      tabs.get(agentId)?.term.write(text)
    },
    onSessionRestart(agentId: string) {
      tabs.get(agentId)?.term.reset()
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
  }

  post({ type: 'ready' })
  debug('xterm.js shell initialized (awaiting host tabs)')
}
