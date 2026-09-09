// Settings Drawer: the right-side panel for user-facing panel settings
// (the claude sandbox toggle and codex's network toggle). Owns its DOM,
// styles, and rendering; main.ts owns the layout slot, the message bridge, and
// drawer exclusivity — the same split as the skills/sessions drawers.
//
// Rows are declared as data (ROWS below) and rendered by one loop: adding a
// setting is one entry plus one field on the payload, not another copy of the
// toggle/desc/note DOM.
//
// No optimistic rendering: a toggle click posts setSetting and the switch
// stays as-is until the host echoes onPanelSettings with what was actually
// saved (the renameSession stance — the UI always reflects persisted state).

import { L } from './i18n'

export type PanelSettingsPayload = {
  sandboxEnabled: boolean
  sandboxSupported: boolean
  codexNetworkEnabled: boolean
}

/** Names the host switches on in its setSetting handler. */
export type SettingKey = 'sandbox' | 'codexNetwork'

/** What one row shows for a given host state. */
type RowView = {
  on: boolean
  /** Dead switch — the OS can't honor it. Clicks are ignored. */
  disabled: boolean
  /** Sub-line under the description; `warn` colors it amber. */
  note: string
  warn: boolean
}

type RowSpec = {
  key: SettingKey
  title: string
  desc: string
  view: (s: PanelSettingsPayload) => RowView
}

const ROWS: RowSpec[] = [
  {
    key: 'sandbox',
    title: L.sandboxTitle,
    desc: L.sandboxDesc,
    // Supported → remind that the flip reaches NEW tabs only (a running
    // session's sandbox was fixed at its spawn); unsupported → say why the
    // switch is dead instead of leaving a mystery-disabled control.
    view: (s) => ({
      on: s.sandboxSupported && s.sandboxEnabled,
      disabled: !s.sandboxSupported,
      note: s.sandboxSupported ? L.sandboxAppliesToNewTabs : L.sandboxUnsupported,
      warn: !s.sandboxSupported,
    }),
  },
  {
    key: 'codexNetwork',
    title: L.codexNetworkTitle,
    desc: L.codexNetworkDesc,
    // No support flag: codex brings its own sandbox on every OS it runs on.
    // ON is the state worth flagging — it drops a guard rather than raising
    // one, so it gets the amber note instead of the neutral one.
    view: (s) => ({
      on: s.codexNetworkEnabled,
      disabled: false,
      note: s.codexNetworkEnabled ? L.codexNetworkOn : L.sandboxAppliesToNewTabs,
      warn: s.codexNetworkEnabled,
    }),
  },
]

export type SettingsDrawer = {
  /** The drawer element to place inside #main (right of #terminals). */
  root: HTMLElement
  /** The ⚙ toggle button to place at the tab bar's right edge. */
  button: HTMLElement
  /** Host push (getSettings response / setSetting echo). */
  handlePanelSettings: (payload: PanelSettingsPayload) => void
  /** Close if open (drawer exclusivity). Fires onToggled like a user close. */
  close: () => void
}

export function createSettingsDrawer(opts: {
  requestSettings: () => void
  setSetting: (key: SettingKey, enabled: boolean) => void
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

  let open = false
  // Last host-pushed state; null until the first onPanelSettings lands
  // (clicks are ignored until then — there is nothing truthful to flip).
  let state: PanelSettingsPayload | null = null

  // Build one row per spec and keep the elements render() has to touch.
  const rows = ROWS.map((spec) => {
    const row = document.createElement('div')
    row.className = 'std-row'

    const rowHead = document.createElement('div')
    rowHead.className = 'std-rowhead'
    const rowTitle = document.createElement('span')
    rowTitle.className = 'std-name'
    rowTitle.textContent = spec.title
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
    desc.textContent = spec.desc
    row.appendChild(desc)

    const note = document.createElement('div')
    note.className = 'std-note'
    row.appendChild(note)

    toggle.addEventListener('click', () => {
      if (!state) return
      const view = spec.view(state)
      if (view.disabled) return
      opts.setSetting(spec.key, !view.on)
    })

    body.appendChild(row)
    return { spec, toggle, note }
  })

  root.appendChild(header)
  root.appendChild(body)

  const button = document.createElement('div')
  button.id = 'settingsbtn'
  button.textContent = '⚙'
  button.title = L.settingsBtn
  button.addEventListener('click', () => toggleDrawer())

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
    for (const { spec, toggle, note } of rows) {
      const view = spec.view(state)
      toggle.classList.toggle('on', view.on)
      toggle.classList.toggle('disabled', view.disabled)
      toggle.setAttribute('aria-checked', String(view.on))
      note.textContent = view.note
      note.classList.toggle('warn', view.warn)
    }
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
