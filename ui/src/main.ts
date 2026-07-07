import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { createSkillsDrawer, type SkillsListPayload } from './skills-drawer'

declare global {
  interface Window {
    eto?: { postMessage: (msg: string) => void }
    CoRhinoPanel?: {
      // The host created an agent (tab); build an xterm for it and focus it.
      onTabCreated: (agentId: string) => void
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

  // Skills Drawer: ⚡ at the tab bar's right edge toggles a fixed-width panel to
  // the right of the terminals. The drawer module owns its DOM; we own the layout
  // slot, the message bridge, and refitting the terminal on open/close.
  const drawer = createSkillsDrawer({
    requestList: (agentId) => post({ type: 'listSkills', agentId }),
    openSkill: (path) => post({ type: 'openSkill', path }),
    reloadSkills: (agentId) => post({ type: 'reloadSkills', agentId }),
    getActiveAgentId: () => activeTab?.agentId ?? null,
    onToggled: () => { if (activeTab) safeFit(activeTab) },
  })
  tabbar.appendChild(drawer.button)

  const mainEl = document.createElement('div')
  mainEl.id = 'main'
  mainEl.appendChild(termsEl)
  mainEl.appendChild(drawer.root)
  rootEl.appendChild(tabbar)
  rootEl.appendChild(mainEl)

  type Tab = {
    agentId: string
    term: Terminal
    fit: FitAddon
    pane: HTMLElement
    tabEl: HTMLElement
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

  function addTab(agentId: string) {
    if (tabs.has(agentId)) {
      activateTab(agentId)
      return
    }
    const pane = document.createElement('div')
    pane.className = 'termpane'
    termsEl.appendChild(pane)

    const { term, fit } = createTerminal()
    const tab: Tab = { agentId, term, fit, pane, tabEl: buildTabButton(agentId), lastCols: term.cols, lastRows: term.rows }
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

  function buildTabButton(agentId: string): HTMLElement {
    const el = document.createElement('div')
    el.className = 'tab'
    const label = document.createElement('span')
    label.textContent = agentId
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
    return el
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
    onTabCreated(agentId: string) {
      addTab(agentId)
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
  }

  post({ type: 'ready' })
  debug('xterm.js shell initialized (awaiting host tabs)')
}
