// Sessions Drawer: the right-side panel listing past claude sessions of the
// shared workspace, resumable with a click. Owns its DOM, styles, and rendering;
// main.ts owns the layout slot, the message bridge, drawer exclusivity, and
// terminal refitting. All titles/names land via textContent (they are
// model/user-authored — never innerHTML).

import { L, relTime } from './i18n'

// The agent activity states the host's status watcher reports (the
// onTabStatus / onSessionsList wire enum). Shared with main.ts's tab glyphs.
export type AgentStatus = 'busy' | 'waiting' | 'idle'

// The status glyph vocabulary (shared by tab bar and drawer rows): ✻ = busy
// (claude's own spinner glyph), ‼ = waiting for approval, ✓ = finished
// unseen, and idle renders nothing — quiet states stay unmarked. Styled and
// animated by main.ts's .statusglyph rules.
export function statusGlyph(state: AgentStatus | 'unseen'): string {
  switch (state) {
    case 'busy': return '✻'
    case 'waiting': return '‼'
    case 'unseen': return '✓'
    default: return ''
  }
}

export type SessionInfo = {
  sessionId: string
  title: string
  customName: string | null
  startedAt: string // ISO-8601 UTC
  lastActivity: string // ISO-8601 UTC
  cli: 'claude' | 'codex' | 'pi' // which CLI recorded it (row badge, matches tab badges)
  isLive: boolean // a live tab (any panel) runs this very session
  // The live tab's activity state; null/absent when the session is live in
  // another panel (this panel can't see that tab's state — fall back to the
  // generic "running").
  status?: AgentStatus | null
  // The parent session's id when an agent spawned this session via the
  // orchestration protocol (from session-parents.json); null/absent for
  // user-created sessions. Resolved to a display name against the same list —
  // the parent may since have been deleted.
  spawnedBy?: string | null
}

export type SessionsListPayload = { sessions: SessionInfo[] }

export type SessionsDrawer = {
  /** The drawer element to place inside #main (right of #terminals). */
  root: HTMLElement
  /** The 🕘 toggle button to place at the tab bar's right edge. */
  button: HTMLElement
  /** Host push / list response. Ignored while closed. */
  handleSessionsList: (payload: SessionsListPayload) => void
  /** Live status transition for one session's row (from onTabStatus). */
  updateStatus: (sessionId: string, status: AgentStatus) => void
  /** Close if open (drawer exclusivity). Fires onToggled like a user close. */
  close: () => void
}

export function createSessionsDrawer(opts: {
  requestList: () => void
  resumeSession: (sessionId: string) => void
  renameSession: (sessionId: string, name: string) => void
  deleteSession: (sessionId: string) => void
  /** Called after open/close so main.ts can refit and enforce exclusivity. */
  onToggled: (open: boolean) => void
}): SessionsDrawer {
  injectStyles()

  const root = document.createElement('div')
  root.id = 'sessionsdrawer'
  root.classList.add('hidden')

  const header = document.createElement('div')
  header.className = 'ss-header'
  const title = document.createElement('span')
  title.textContent = 'Sessions'
  header.appendChild(title)

  const list = document.createElement('div')
  list.className = 'ss-list'

  root.appendChild(header)
  root.appendChild(list)

  const button = document.createElement('div')
  button.id = 'sessionsbtn'
  button.textContent = '🕘'
  button.title = L.sessionsBtn
  button.addEventListener('click', () => toggle())

  let open = false

  function toggle() {
    open = !open
    root.classList.toggle('hidden', !open)
    button.classList.toggle('active', open)
    if (open) opts.requestList()
    opts.onToggled(open)
  }

  function close() {
    if (open) toggle()
  }

  function handleSessionsList(payload: SessionsListPayload) {
    if (!open) return
    render(payload.sessions)
  }

  function statusLabel(status: AgentStatus | null | undefined): string {
    switch (status) {
      case 'busy': return L.statusBusy
      case 'waiting': return L.statusWaiting
      case 'idle': return L.statusIdle
      default: return L.running
    }
  }

  // Patch one live row's glyph/label in place. Rows only carry the sub-line
  // while live, so a session that JUST became live is simply skipped — the
  // list push that follows a spawn re-renders it whole. Never touches an
  // in-flight rename input (the patch is outside the name span).
  function updateStatus(sessionId: string, status: AgentStatus) {
    const row = list.querySelector(`.ss-row[data-session-id="${CSS.escape(sessionId)}"]`)
    if (!row) return
    const state = row.querySelector('.ss-state.live')
    if (!state) return
    state.textContent = statusLabel(status)
    const glyph = row.querySelector('.ss-sub .statusglyph')
    if (glyph) {
      glyph.className = `statusglyph ${status}`
      glyph.textContent = statusGlyph(status)
    }
  }

  function render(sessions: SessionInfo[]) {
    // A push can land while the user is typing a name (scans run on any tab
    // change); clearing the list would silently destroy the edit (removing a
    // focused element fires no blur on WebKit). Skip — the rename commit
    // triggers its own push, which re-renders with fresh data anyway.
    if (list.querySelector('.ss-renameinput')) return
    list.textContent = ''
    if (sessions.length === 0) {
      const empty = document.createElement('div')
      empty.className = 'ss-empty'
      empty.textContent = L.sessionsEmpty
      list.appendChild(empty)
      return
    }
    // Lineage arrives as raw parent ids; resolve display names against this
    // very list so a rename/auto-title is always current (no name duplication
    // in the sidecar).
    const byId = new Map(sessions.map((s) => [s.sessionId, s]))
    for (const session of sessions) {
      const parent = session.spawnedBy ? byId.get(session.spawnedBy) : undefined
      list.appendChild(buildRow(session, parent ? (parent.customName ?? parent.title) : null))
    }
  }

  function buildRow(session: SessionInfo, spawnParentName: string | null): HTMLElement {
    const blocked = session.isLive

    const row = document.createElement('div')
    row.className = blocked ? 'ss-row blocked' : 'ss-row'
    // updateStatus finds this row by id for live dot updates (ids are UUIDs —
    // selector-safe, but escaped anyway).
    row.dataset.sessionId = session.sessionId

    const head = document.createElement('div')
    head.className = 'ss-rowhead'
    // CLI badge, same glyphs/colors as the tab bar (styles from main.ts).
    const badge = document.createElement('span')
    badge.className = `clibadge ${session.cli === 'codex' ? 'codex' : 'claude'}`
    badge.textContent = session.cli === 'codex' ? '◆' : '●'
    badge.title = session.cli === 'codex' ? 'Codex' : 'Claude Code'
    head.appendChild(badge)
    if (session.spawnedBy) {
      // Agent-spawned marker, same visual slot as the CLI badge. The subline
      // carries the parent's name; this is the at-a-glance flag.
      const spawn = document.createElement('span')
      spawn.className = 'spawnbadge'
      spawn.textContent = '⧉'
      spawn.title = L.spawnBadgeTitle
      head.appendChild(spawn)
    }
    const name = document.createElement('span')
    name.className = 'ss-name'
    name.textContent = session.customName ?? session.title
    const time = document.createElement('span')
    time.className = 'ss-time'
    time.textContent = relTime(session.lastActivity)
    const rename = document.createElement('span')
    rename.className = 'ss-rename'
    rename.textContent = '✎'
    rename.title = L.renameSession
    rename.addEventListener('click', (e) => {
      e.stopPropagation()
      startRename(session, name)
    })
    head.appendChild(name)
    head.appendChild(time)
    head.appendChild(rename)
    // No delete on a live session — the host would reject it anyway; hiding the
    // affordance says "close the tab first" better than a dead button.
    if (!session.isLive) head.appendChild(buildDeleteButton(session))
    row.appendChild(head)

    const hasSub = session.isLive || session.customName || session.spawnedBy
    if (hasSub) {
      const sub = document.createElement('div')
      sub.className = 'ss-sub'
      if (session.isLive) {
        // Status glyph + label when this panel owns the live tab; the generic
        // "running" when another panel does (status is null there). The glyph
        // span exists even when idle renders it empty — updateStatus patches
        // it in place on later transitions.
        if (session.status) {
          const glyph = document.createElement('span')
          glyph.className = `statusglyph ${session.status}`
          glyph.textContent = statusGlyph(session.status)
          sub.appendChild(glyph)
        }
        const state = document.createElement('span')
        state.className = 'ss-state live'
        state.textContent = statusLabel(session.status)
        sub.appendChild(state)
      }
      if (session.customName) {
        // The auto title still carries context when a custom name replaced it.
        const auto = document.createElement('span')
        auto.className = 'ss-autotitle'
        auto.textContent = session.title
        sub.appendChild(auto)
      }
      if (session.spawnedBy) {
        const spawned = document.createElement('span')
        spawned.className = 'ss-spawned'
        // Parent name via textContent — it is user/model-authored.
        spawned.textContent = spawnParentName ? L.spawnedFrom(spawnParentName) : L.spawnedOrphan
        sub.appendChild(spawned)
      }
      row.appendChild(sub)
    }

    if (!blocked) {
      row.title = L.clickToResume
      row.addEventListener('click', () => opts.resumeSession(session.sessionId))
    } else {
      row.title = L.liveRowHint
    }
    return row
  }

  // Two-step confirm: first click arms the button (shows 削除?), second click
  // within 3s deletes. Native confirm() dialogs don't render reliably in the
  // embedded WebView, and a lost transcript is unrecoverable — the arm step is
  // the consent.
  function buildDeleteButton(session: SessionInfo): HTMLElement {
    const del = document.createElement('span')
    del.className = 'ss-delete'
    del.textContent = '🗑'
    del.title = L.deleteSession
    let armed = false
    let timer: number | undefined
    const disarm = () => {
      armed = false
      if (timer !== undefined) window.clearTimeout(timer)
      del.classList.remove('armed')
      del.textContent = '🗑'
    }
    del.addEventListener('click', (e) => {
      e.stopPropagation()
      if (!armed) {
        armed = true
        del.classList.add('armed')
        del.textContent = L.deleteConfirm
        timer = window.setTimeout(disarm, 3000)
        return
      }
      disarm()
      opts.deleteSession(session.sessionId)
      // The host deletes, rescans, and pushes a fresh list — no optimistic
      // removal, so a rejected delete (e.g. raced a resume) self-corrects.
    })
    return del
  }

  // Swap the name span for an inline input. Enter = commit, Esc = cancel,
  // blur = commit; empty commits revert to the auto title (host removes the
  // custom name). Events are stopped so the row's resume click never fires.
  function startRename(session: SessionInfo, name: HTMLElement) {
    const input = document.createElement('input')
    input.className = 'ss-renameinput'
    input.value = session.customName ?? ''
    input.placeholder = session.title
    input.addEventListener('click', (e) => e.stopPropagation())
    input.addEventListener('dblclick', (e) => e.stopPropagation())
    let done = false
    const finish = (commit: boolean) => {
      if (done) return
      done = true
      if (commit) {
        opts.renameSession(session.sessionId, input.value.trim())
        // The host re-pushes the list after persisting; no optimistic render.
      }
      input.replaceWith(name)
    }
    input.addEventListener('keydown', (e) => {
      e.stopPropagation()
      if (e.key === 'Enter') finish(true)
      else if (e.key === 'Escape') finish(false)
    })
    input.addEventListener('blur', () => finish(true))
    name.replaceWith(input)
    input.focus()
    input.select()
  }

  return { root, button, handleSessionsList, updateStatus, close }
}

function injectStyles() {
  const style = document.createElement('style')
  style.textContent = `
  #sessionsbtn {
    display: flex; align-items: center; justify-content: center; min-width: 28px; height: 100%;
    color: #aaa; cursor: pointer; border-left: 1px solid #2a2a2a;
    font-size: 12px; line-height: 1; flex: 0 0 auto;
  }
  #sessionsbtn:hover { background: #222; color: #fff; }
  #sessionsbtn.active { background: #111; }
  #sessionsdrawer {
    flex: 0 0 260px; display: flex; flex-direction: column; overflow: hidden;
    background: #1a1a1a; border-left: 1px solid #2a2a2a;
    font: 12px ui-monospace, Menlo, monospace; color: #ccc; user-select: none;
  }
  #sessionsdrawer.hidden { display: none; }
  .ss-header {
    display: flex; align-items: center; justify-content: space-between;
    flex: 0 0 28px; padding: 0 10px; border-bottom: 1px solid #2a2a2a; color: #ddd;
  }
  .ss-list { flex: 1 1 auto; overflow-y: auto; }
  .ss-empty { padding: 12px 10px; color: #888; line-height: 1.6; user-select: text; }
  .ss-row { border-bottom: 1px solid #222; padding: 6px 10px; cursor: pointer; }
  .ss-row:hover { background: #222; }
  .ss-row.blocked { cursor: default; opacity: 0.55; }
  .ss-row.blocked:hover { background: none; }
  .ss-rowhead { display: flex; align-items: center; gap: 6px; }
  .ss-name {
    flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #ddd;
  }
  .ss-time { flex: 0 0 auto; color: #888; font-size: 11px; }
  .ss-rename { flex: 0 0 auto; color: #777; cursor: pointer; padding: 0 2px; border-radius: 2px; }
  .ss-rename:hover { color: #fff; background: #333; }
  .ss-delete {
    flex: 0 0 auto; color: #777; cursor: pointer; padding: 0 2px; border-radius: 2px;
    font-size: 10px; white-space: nowrap;
  }
  .ss-delete:hover { color: #fff; background: #333; }
  .ss-delete.armed { color: #fff; background: #a33; }
  .ss-delete.armed:hover { background: #c44; }
  .ss-renameinput {
    flex: 1 1 auto; min-width: 0; background: #111; color: #ddd;
    border: 1px solid #444; border-radius: 2px; padding: 1px 4px;
    font: 12px ui-monospace, Menlo, monospace; outline: none;
  }
  .ss-sub {
    display: flex; align-items: center; gap: 6px; margin-top: 3px;
    overflow: hidden; white-space: nowrap;
  }
  .ss-state { flex: 0 0 auto; font-size: 10px; color: #999; }
  .ss-state.live { color: #6ec26e; }
  .ss-autotitle {
    flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis;
    color: #777; font-size: 11px;
  }
  .spawnbadge { flex: 0 0 auto; font-size: 10px; color: #9a7fd0; }
  .ss-spawned {
    flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis;
    color: #8a7ab0; font-size: 11px;
  }
`
  document.head.appendChild(style)
}
