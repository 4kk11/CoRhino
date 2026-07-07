// Skills Drawer: the right-side panel listing the skills loaded into the
// embedded claude sessions (user-made ~/.corhino/skills + bundled cc-plugin).
// Owns its DOM, styles, and rendering; main.ts owns the layout slot, the
// message bridge, and terminal refitting. All skill text lands via textContent
// (names/descriptions are user-authored — never innerHTML).

export type SkillInfo = {
  name: string
  description: string
  source: 'user' | 'bundled'
  path: string
  needsReload: boolean
}

export type SkillsListPayload = { agentId: string; skills: SkillInfo[] }

export type SkillsDrawer = {
  /** The drawer element to place inside #main (right of #terminals). */
  root: HTMLElement
  /** The ⚡ toggle button to place at the tab bar's right edge. */
  button: HTMLElement
  /** Host push / list response. Ignored while closed or for a non-active tab. */
  handleSkillsList: (payload: SkillsListPayload) => void
  /** Active tab changed (switch/reset+respawn): re-request if open. */
  notifyActiveTabChanged: () => void
  /** Close if open (drawer exclusivity). Fires onToggled like a user close. */
  close: () => void
}

export function createSkillsDrawer(opts: {
  requestList: (agentId: string) => void
  openSkill: (path: string) => void
  reloadSkills: (agentId: string) => void
  getActiveAgentId: () => string | null
  /** Called after open/close so main.ts can refit and enforce exclusivity. */
  onToggled: (open: boolean) => void
}): SkillsDrawer {
  injectStyles()

  const root = document.createElement('div')
  root.id = 'skillsdrawer'
  root.classList.add('hidden')

  const header = document.createElement('div')
  header.className = 'sd-header'
  const title = document.createElement('span')
  title.textContent = 'Skills'
  const reloadBtn = document.createElement('span')
  reloadBtn.className = 'sd-reload'
  reloadBtn.textContent = '⟳'
  reloadBtn.title = 'アクティブなセッションにスキルを再読み込み (/plugin reload-plugins)'
  reloadBtn.addEventListener('click', () => {
    const id = opts.getActiveAgentId()
    if (id) opts.reloadSkills(id)
  })
  header.appendChild(title)
  header.appendChild(reloadBtn)

  const list = document.createElement('div')
  list.className = 'sd-list'

  root.appendChild(header)
  root.appendChild(list)

  const button = document.createElement('div')
  button.id = 'skillsbtn'
  button.textContent = '⚡'
  button.title = 'スキル一覧'
  button.addEventListener('click', () => toggle())

  let open = false

  function toggle() {
    open = !open
    root.classList.toggle('hidden', !open)
    button.classList.toggle('active', open)
    if (open) {
      const id = opts.getActiveAgentId()
      if (id) opts.requestList(id)
    }
    opts.onToggled(open)
  }

  function close() {
    if (open) toggle()
  }

  function handleSkillsList(payload: SkillsListPayload) {
    if (!open) return
    // Watcher pushes arrive once per tab; render only the active tab's view.
    if (payload.agentId !== opts.getActiveAgentId()) return
    render(payload.skills)
  }

  function notifyActiveTabChanged() {
    if (!open) return
    const id = opts.getActiveAgentId()
    if (id) opts.requestList(id)
  }

  function render(skills: SkillInfo[]) {
    list.textContent = ''
    if (skills.length === 0) {
      const empty = document.createElement('div')
      empty.className = 'sd-empty'
      empty.textContent =
        'まだスキルがありません。claude に「今の手順をスキルにして」と頼むと保存されます。'
      list.appendChild(empty)
      return
    }
    for (const skill of skills) list.appendChild(buildRow(skill))
  }

  function buildRow(skill: SkillInfo): HTMLElement {
    const row = document.createElement('div')
    row.className = 'sd-row'

    const head = document.createElement('div')
    head.className = 'sd-rowhead'
    const dot = document.createElement('span')
    dot.className = skill.needsReload ? 'sd-dot stale' : 'sd-dot'
    dot.textContent = skill.needsReload ? '⚠' : '●'
    const name = document.createElement('span')
    name.className = 'sd-name'
    name.textContent = skill.name
    const badge = document.createElement('span')
    badge.className = 'sd-badge'
    badge.textContent = skill.source === 'user' ? '自作' : '同梱'
    head.appendChild(dot)
    head.appendChild(name)
    head.appendChild(badge)
    row.appendChild(head)

    if (skill.needsReload) {
      const hint = document.createElement('div')
      hint.className = 'sd-stalehint'
      hint.textContent = '要 reload（⟳ で反映）'
      row.appendChild(hint)
    }

    const detail = document.createElement('div')
    detail.className = 'sd-detail'
    const desc = document.createElement('div')
    desc.className = 'sd-desc'
    desc.textContent = skill.description || '(説明なし)'
    const openLink = document.createElement('div')
    openLink.className = 'sd-open'
    openLink.textContent = '↗ エディタで開く'
    openLink.addEventListener('click', (e) => {
      e.stopPropagation()
      opts.openSkill(skill.path)
    })
    detail.appendChild(desc)
    detail.appendChild(openLink)
    row.appendChild(detail)

    head.addEventListener('click', () => row.classList.toggle('expanded'))
    return row
  }

  return { root, button, handleSkillsList, notifyActiveTabChanged, close }
}

function injectStyles() {
  const style = document.createElement('style')
  style.textContent = `
  #skillsbtn {
    display: flex; align-items: center; justify-content: center; min-width: 28px; height: 100%;
    color: #aaa; cursor: pointer; border-left: 1px solid #2a2a2a;
    font-size: 13px; line-height: 1; flex: 0 0 auto;
  }
  #skillsbtn:hover { background: #222; color: #fff; }
  #skillsbtn.active { background: #111; color: #ffd75f; }
  #skillsdrawer {
    flex: 0 0 260px; display: flex; flex-direction: column; overflow: hidden;
    background: #1a1a1a; border-left: 1px solid #2a2a2a;
    font: 12px ui-monospace, Menlo, monospace; color: #ccc; user-select: none;
  }
  #skillsdrawer.hidden { display: none; }
  .sd-header {
    display: flex; align-items: center; justify-content: space-between;
    flex: 0 0 28px; padding: 0 10px; border-bottom: 1px solid #2a2a2a; color: #ddd;
  }
  .sd-reload { cursor: pointer; color: #aaa; padding: 2px 4px; border-radius: 3px; }
  .sd-reload:hover { background: #2a2a2a; color: #fff; }
  .sd-list { flex: 1 1 auto; overflow-y: auto; }
  .sd-empty { padding: 12px 10px; color: #888; line-height: 1.6; user-select: text; }
  .sd-row { border-bottom: 1px solid #222; }
  .sd-rowhead {
    display: flex; align-items: center; gap: 6px; padding: 6px 10px; cursor: pointer;
  }
  .sd-rowhead:hover { background: #222; }
  .sd-dot { color: #6ec26e; flex: 0 0 auto; font-size: 10px; }
  .sd-dot.stale { color: #ffd75f; }
  .sd-name {
    flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #ddd;
  }
  .sd-badge {
    flex: 0 0 auto; font-size: 10px; color: #999;
    border: 1px solid #333; border-radius: 3px; padding: 0 4px;
  }
  .sd-stalehint { padding: 0 10px 6px 26px; color: #ffd75f; font-size: 11px; }
  .sd-detail { display: none; padding: 0 10px 8px 26px; }
  .sd-row.expanded .sd-detail { display: block; }
  .sd-desc { color: #aaa; line-height: 1.5; white-space: pre-wrap; user-select: text; }
  .sd-open { margin-top: 6px; color: #6fa8dc; cursor: pointer; }
  .sd-open:hover { text-decoration: underline; }
`
  document.head.appendChild(style)
}
