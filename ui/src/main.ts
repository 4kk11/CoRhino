import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'

declare global {
  interface Window {
    eto?: { postMessage: (msg: string) => void }
    ClaudePanel?: { onPtyData: (text: string) => void }
    chrome?: { webview?: { postMessage: (msg: string) => void } }
  }
}

type Outbound =
  | { type: 'ready' }
  | { type: 'stdin'; data: string }
  | { type: 'resize'; cols: number; rows: number }
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
    console.error('[Claude] no postMessage bridge available')
  } catch (e) {
    console.error('[Claude] post failed', e)
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

const style = document.createElement('style')
style.textContent = `
  html, body { margin: 0; padding: 0; height: 100%; width: 100%; background: #111; overflow: hidden; }
  #terminal { width: 100%; height: 100%; padding: 4px; box-sizing: border-box; overflow: hidden; }
  .xterm, .xterm-viewport { background: #111 !important; }
  /* During Japanese IME composition the helper textarea grows to fit the composing text.
     On Chromium/WebView2 (unlike macOS WKWebView) that growth bubbles up as document
     overflow, scrolling the whole panel sideways. Clipping the helper textarea keeps
     the layout stable and prevents the cascade into FitAddon → PTY resize → TUI redraw. */
  .xterm-helper-textarea { max-width: 0; max-height: 0; overflow: hidden !important; }
`
document.head.appendChild(style)

const container = document.getElementById('terminal')!
const rect = container.getBoundingClientRect()
debug(`#terminal rect: ${rect.width}x${rect.height}`)

const term = new Terminal({
  fontFamily: 'ui-monospace, Menlo, monospace',
  fontSize: 12,
  cursorBlink: true,
  // macOS PTY's line discipline converts \n to \r\n before output reaches us.
  // Windows ConPTY does not, so when claude.exe writes a bare \n the cursor
  // stays on the same column and subsequent text staircases off the right edge.
  // Enabling convertEol applies the \r\n synthesis in xterm regardless of host.
  convertEol: true,
  theme: {
    background: '#111111',
    foreground: '#dddddd',
  },
})
const fit = new FitAddon()
term.loadAddon(fit)
try {
  term.open(container)
  debug(`term.open ok. cols=${term.cols} rows=${term.rows}`)
} catch (e: any) {
  debug(`term.open failed: ${e?.stack ?? e}`)
  throw e
}
try {
  fit.fit()
  debug(`fit.fit ok. cols=${term.cols} rows=${term.rows}`)
} catch (e: any) {
  debug(`fit.fit failed: ${e?.stack ?? e}`)
}

term.onData((data) => post({ type: 'stdin', data }))
term.onResize(({ cols, rows }) => post({ type: 'resize', cols, rows }))

window.ClaudePanel = {
  onPtyData(text: string) {
    term.write(text)
  },
}

// Debounced fit. Without debouncing, a single resize can chain into a feedback loop:
// claude paints → scrollbar appears → ResizeObserver fires → fit() shrinks cols →
// PTY resize → claude repaints → loop. The guard on (cols,rows) change also
// avoids posting redundant resize events to the host when only sub-cell pixels shifted.
let resizeTimer: number | undefined
let lastCols = term.cols
let lastRows = term.rows
const safeFit = () => {
  try {
    fit.fit()
    if (term.cols !== lastCols || term.rows !== lastRows) {
      lastCols = term.cols
      lastRows = term.rows
    }
  } catch {}
}
const resizeObserver = new ResizeObserver(() => {
  if (resizeTimer !== undefined) window.clearTimeout(resizeTimer)
  resizeTimer = window.setTimeout(safeFit, 80)
})
resizeObserver.observe(container)

post({ type: 'ready' })
post({ type: 'resize', cols: term.cols, rows: term.rows })

debug(`xterm.js initialized ${term.cols}x${term.rows}`)
}
