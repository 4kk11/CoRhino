import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'

declare global {
  interface Window {
    eto?: { postMessage: (msg: string) => void }
    ClaudePanel?: { onPtyData: (text: string) => void }
  }
}

type Outbound =
  | { type: 'ready' }
  | { type: 'stdin'; data: string }
  | { type: 'resize'; cols: number; rows: number }
  | { type: 'debug'; message: string }

function post(msg: Outbound) {
  try {
    window.eto?.postMessage(JSON.stringify(msg))
  } catch (e) {
    console.error('[Claude] post failed', e)
  }
}

function debug(message: string) {
  post({ type: 'debug', message })
}

window.addEventListener('error', (ev) => {
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
  html, body { margin: 0; padding: 0; height: 100%; background: #111; }
  #terminal { width: 100%; height: 100%; padding: 4px; box-sizing: border-box; }
  .xterm, .xterm-viewport { background: #111 !important; }
`
document.head.appendChild(style)

const container = document.getElementById('terminal')!
const rect = container.getBoundingClientRect()
debug(`#terminal rect: ${rect.width}x${rect.height}`)

const term = new Terminal({
  fontFamily: 'ui-monospace, Menlo, monospace',
  fontSize: 12,
  cursorBlink: true,
  convertEol: false,
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

const resizeObserver = new ResizeObserver(() => {
  try { fit.fit() } catch {}
})
resizeObserver.observe(document.getElementById('terminal')!)

post({ type: 'ready' })
post({ type: 'resize', cols: term.cols, rows: term.rows })

debug(`xterm.js initialized ${term.cols}x${term.rows}`)
}
