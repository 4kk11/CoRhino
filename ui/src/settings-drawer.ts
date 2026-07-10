// Settings Drawer: the right-side panel for user-facing panel settings
// (currently: the claude sandbox toggle). Owns its DOM, styles, and rendering;
// main.ts owns the layout slot, the message bridge, and drawer exclusivity —
// the same split as the skills/sessions drawers.
//
// No optimistic rendering: a toggle click posts setSandbox and the switch
// stays as-is until the host echoes onPanelSettings with what was actually
// saved (the renameSession stance — the UI always reflects persisted state).

import { L } from './i18n'

export type PanelSettingsPayload = { sandboxEnabled: boolean; sandboxSupported: boolean }

export type SettingsDrawer = {
  /** The drawer element to place inside #main (right of #terminals). */
  root: HTMLElement
  /** The ⚙ toggle button to place at the tab bar's right edge. */
  button: HTMLElement
  /** Host push (getSettings response / setSandbox echo). */
  handlePanelSettings: (payload: PanelSettingsPayload) => void
  /** Close if open (drawer exclusivity). Fires onToggled like a user close. */
  close: () => void
}

export function createSettingsDrawer(opts: {
  requestSettings: () => void
  setSandbox: (enabled: boolean) => void
  /** Called after open/close so main.ts can refit and enforce exclusivity. */
  onToggled: (open: boolean) => void
}): SettingsDrawer {
  injectStyles()

  const root = document.createElement('div')
  root.id = 'settingsdrawer'
  root.classList.add('hidden')

  const header = document.createElement('div')
  header.className = 'std-header'
  const title = document.createElement('span')
  title.textContent = L.settingsTitle
  header.appendChild(title)

  const body = document.createElement('div')
  body.className = 'std-body'

  // --- Sandbox section -------------------------------------------------
  const row = document.createElement('div')
  row.className = 'std-row'

  const rowHead = document.createElement('div')
  rowHead.className = 'std-rowhead'
  const rowTitle = document.createElement('span')
  rowTitle.className = 'std-name'
  rowTitle.textContent = L.sandboxTitle
  const toggle = document.createElement('div')
  toggle.className = 'std-toggle'
  toggle.setAttribute('role', 'switch')
  const knob = document.createElement('div')
  knob.className = 'std-knob'
  toggle.appendChild(knob)
  rowHead.appendChild(rowTitle)
  rowHead.appendChild(toggle)
  row.appendChild(rowHead)

  const desc = document.createElement('div')
  desc.className = 'std-desc'
  desc.textContent = L.sandboxDesc
  row.appendChild(desc)

  const note = document.createElement('div')
  note.className = 'std-note'
  row.appendChild(note)

  body.appendChild(row)

  root.appendChild(header)
  root.appendChild(body)

  const button = document.createElement('div')
  button.id = 'settingsbtn'
  button.textContent = '⚙'
  button.title = L.settingsBtn
  button.addEventListener('click', () => toggleDrawer())

  let open = false
  // Last host-pushed state; null until the first onPanelSettings lands
  // (clicks are ignored until then — there is nothing truthful to flip).
  let state: PanelSettingsPayload | null = null

  toggle.addEventListener('click', () => {
    if (!state || !state.sandboxSupported) return
    opts.setSandbox(!state.sandboxEnabled)
  })

  function toggleDrawer() {
    open = !open
    root.classList.toggle('hidden', !open)
    button.classList.toggle('active', open)
    if (open) opts.requestSettings()
    opts.onToggled(open)
  }

  function close() {
    if (open) toggleDrawer()
  }

  function handlePanelSettings(payload: PanelSettingsPayload) {
    state = payload
    render()
  }

  function render() {
    if (!state) return
    const { sandboxEnabled, sandboxSupported } = state
    toggle.classList.toggle('on', sandboxSupported && sandboxEnabled)
    toggle.classList.toggle('disabled', !sandboxSupported)
    toggle.setAttribute('aria-checked', String(sandboxSupported && sandboxEnabled))
    // Supported → remind that the flip reaches NEW tabs only (a running
    // session's sandbox was fixed at its spawn); unsupported → say why the
    // switch is dead instead of leaving a mystery-disabled control.
    note.textContent = sandboxSupported ? L.sandboxAppliesToNewTabs : L.sandboxUnsupported
    note.classList.toggle('warn', !sandboxSupported)
  }

  return { root, button, handlePanelSettings, close }
}

function injectStyles() {
  const style = document.createElement('style')
  style.textContent = `
  #settingsbtn {
    display: flex; align-items: center; justify-content: center; min-width: 28px; height: 100%;
    color: #aaa; cursor: pointer; border-left: 1px solid #2a2a2a;
    font-size: 13px; line-height: 1; flex: 0 0 auto;
  }
  #settingsbtn:hover { background: #222; color: #fff; }
  #settingsbtn.active { background: #111; color: #8fbf8f; }
  #settingsdrawer {
    flex: 0 0 260px; display: flex; flex-direction: column; overflow: hidden;
    background: #1a1a1a; border-left: 1px solid #2a2a2a;
    font: 12px ui-monospace, Menlo, monospace; color: #ccc; user-select: none;
  }
  #settingsdrawer.hidden { display: none; }
  .std-header {
    display: flex; align-items: center;
    flex: 0 0 28px; padding: 0 10px; border-bottom: 1px solid #2a2a2a; color: #ddd;
  }
  .std-body { flex: 1 1 auto; overflow-y: auto; }
  .std-row { border-bottom: 1px solid #222; padding: 8px 10px; }
  .std-rowhead { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .std-name { color: #ddd; }
  .std-desc { margin-top: 6px; color: #aaa; line-height: 1.5; white-space: pre-wrap; }
  .std-note { margin-top: 6px; color: #888; line-height: 1.5; }
  .std-note.warn { color: #b8874f; }
  .std-toggle {
    position: relative; flex: 0 0 auto; width: 30px; height: 16px;
    background: #333; border: 1px solid #444; border-radius: 9px; cursor: pointer;
    transition: background .15s;
  }
  .std-toggle .std-knob {
    position: absolute; top: 1px; left: 1px; width: 12px; height: 12px;
    background: #999; border-radius: 50%; transition: left .15s, background .15s;
  }
  .std-toggle.on { background: #2d4a2d; border-color: #3f663f; }
  .std-toggle.on .std-knob { left: 15px; background: #8fbf8f; }
  .std-toggle.disabled { opacity: 0.35; cursor: default; }
`
  document.head.appendChild(style)
}
