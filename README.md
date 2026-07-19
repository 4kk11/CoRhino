# CoRhino

<p align="center">
  <img src="src/Resources/icon.png" alt="CoRhino" width="120">
</p>

<p align="center">
  English | <a href="README.ja.md">日本語</a>
</p>

A Rhino 8 plugin that **docks an AI coding agent into Rhino**. Agents (Claude Code / Codex CLI, selectable per tab) live in a side panel and operate the very Rhino instance they are docked in.

The goal is that non-engineers can use it by just opening the panel — no `MCPConnect`, no config-file pasting, no manual MCP setup. All Rhino tools/agents live in the official [Rhino-MCP-Platform](https://github.com/mcneel/RhinoMCP); CoRhino only provides the **panel UX, launch wiring, and onboarding** (it ships no tools of its own).

https://github.com/user-attachments/assets/661a35e1-c61b-4d1d-92d9-56ed656b872f

> [!WARNING]
> Pre-alpha / WIP. Commands, protocol, and configuration may change without notice.

## How it works

```
Rhino process
├── CoRhino plugin (.rhp)
│    └── side panel = WebView + xterm.js tabs
│         └── each tab runs an agent CLI (claude / codex) via PTY
│              └── MCP over direct HTTP → http://localhost:<port>/
└── Rhino-MCP-Platform plugin (.rhp)
     └── MCP host serving Rhino tools at http://localhost:<port>/
```

- Each panel gets its **own port** (base 10500), so with multiple documents each panel controls its own Rhino.
- Agents connect to the Rhino-MCP-Platform host via **direct HTTP** — no node router in between.
- Agent sessions are **isolated**: per-tab MCP config is injected at launch; your `~/.claude` and `~/.codex` stay untouched (no re-login needed).

## Requirements

- **Rhino 8.32 or later** — macOS (Apple Silicon) is the recommended platform; Intel Mac and Windows also work
- **[Claude Code](https://claude.com/claude-code) CLI** (`claude`) on your PATH
  - If missing, the tab shows a setup view that runs the official installer with one click
- (Optional) **[Codex CLI](https://developers.openai.com/codex/cli)** (`codex`) **0.142 or later** — enables codex tabs in the "+" menu; grayed out if absent, with no effect on claude tabs
  - Older versions fail to launch codex tabs (unknown hook flags) — fix with `codex update`
- **Rhino-MCP-Platform** (yak package) — if missing, the panel detects it and handles consent → install → restart guidance automatically

## Installation

Install from the Rhino Package Manager:

1. In Rhino, run `_PackageManager` (Tools ▸ Package Manager)
2. Search for **CoRhino** and install
3. Restart Rhino

To build from source instead, see [For developers](#for-developers).

## Usage

1. Run the `CoRhino` command → the side panel opens
2. First run: if Rhino-MCP-Platform is missing, a consent dialog installs it → restart Rhino
3. Ask the agent things like *"What's in the current document?"*

Typing `/exit` (or `Ctrl-D`) in a tab shuts down that agent and closes the tab; when the last tab exits, the panel closes too. Running `CoRhino` again reopens the panel with a fresh session (the instance's MCP host and port are reused). If the panel is already open, `CoRhino` just brings it to the front.

The panel UI, dialogs, and generated templates follow Rhino's language setting (Japanese Rhino → Japanese; otherwise English).

## Features

### Parallel agents (tabs)

"+" on the tab bar launches another agent (claude / codex, mixable) against the **same Rhino document**. Each tab is an independent session with a dedicated layer prefix (`s-XXXXXXXX/`): agents write only under their own layer and treat other layers as read-only, so parallel work (e.g. one generating geometry, one analyzing) rarely collides. "×" on a tab terminates just that agent.

Status glyphs next to the CLI badge (● claude / ◆ codex) show each tab's state without opening it: `✻` (amber, spinning) = working, `‼` (red, blinking) = **waiting for approval**, `✓` (green) = finished while inactive, unread; nothing when idle.

> Note: layer separation is a **convention** enforced via CLAUDE.md / AGENTS.md, not by the host. Touching the same object across layers can still conflict logically, and agents don't see each other's changes in real time.

### Session management (🕘)

Conversations are auto-saved by the CLI. The 🕘 drawer lists past sessions (auto-titled, timestamped) for one-click resume, renaming (double-click the tab label, or ✎), and deletion (🗑, press twice to confirm). All sessions share the same workspace (`~/.corhino/workspace/`), so history survives Rhino restarts. claude and codex sessions appear in one timeline with CLI badges and resume in the matching CLI.

### Custom skills (⚡)

Your own skills live in `~/.corhino/skills/` (a skeleton is seeded on first launch, with a README explaining the format) and load into every session of **both CLIs**. Ask the in-panel Claude "turn what we just did into a skill" and the bundled `save-rhino-skill` meta-skill writes `skills/<name>/SKILL.md` for you (writing it by hand also works).

The ⚡ drawer lists all skills (custom/bundled badges, descriptions) next to the terminal. Newly saved skills appear live; ⚠ marks those not yet active in a session — claude tabs reload them with the ⟳ button in the header, codex tabs pick them up from the next tab (running codex sessions can't reload).

### Agent orchestration

An agent can **spawn child tabs and delegate work to them**. Ask "hand this off to another agent" and the bundled `orchestrate-agents` skill spawns a child tab, sends instructions, detects completion, collects the result, and closes the child.

- No extra approval dialog — the Bash execution that drives spawning is already gated by the parent agent's permission mode. New tabs and Rhino command-line notifications make spawns visible (with a warning if a permissive mode like `acceptEdits` was requested).
- An agent can only control **tabs it created**; user-opened tabs are off-limits.
- Children spawn in the background without stealing focus; progress shows via the tab status glyphs.
- Spawned sessions carry a `⧉` badge ("↳ spawned by …") in the 🕘 drawer.
- Max 8 tabs total; children are Claude Code only in V1.

### Settings (⚙) — sandbox

The ⚙ drawer holds a **sandbox** toggle (saved to `~/.corhino/settings.json`, default OFF). When ON, agent shell commands run OS-sandboxed with file writes confined to `~/.corhino`; legitimate operations outside just fall back to the normal approval flow. **Rhino model operations via MCP are unaffected** — the model is still protected by the layer convention and Undo.

- Applies to **new tabs** only (not running sessions)
- **macOS only** (uses Claude Code's built-in Seatbelt sandbox; the toggle is disabled on Windows)
- **Claude Code tabs only** — codex tabs are always sandboxed by design (`workspace-write`, paired with no approval prompts) and are not affected by the toggle

## For developers

### Build

The UI (vite) build is integrated into the .NET build — one command does everything:

```bash
make build     # = dotnet build src/CoRhino.csproj
```

- The `BuildUi` target runs `npm install` (first time only) + `npm run build` in `ui/`
- Output lands next to the `.rhp` as `web/`, then PostBuild copies everything to the Rhino plugin dir (macOS: `~/Library/Application Support/McNeel/Rhinoceros/8.0/MacPlugIns/CoRhino`)
- macOS bundles a universal (arm64/x64) native dylib via `lipo` — shipping only one architecture breaks `dlopen`

> Never edit the copied plugin dir directly; always rebuild.

### Development commands

Everything is collected in the `Makefile` (`make help` for the list).

| target | what it does |
|---|---|
| `make build` / `make release` | Debug / Release build (UI build + native flatten + install to plugin dir) |
| `make ui-dev` / `make ui-build` | vite dev server / UI-only build |
| `make pack` | Release build + yak package into `dist/yak/` (`make pack VERSION=0.1.0` to pin) |
| `make yak-login` / `make yak-push-test` / `make yak-push` | yak auth / push to test / production server |
| `make clean` / `make uninstall` | remove build outputs / remove installed plugin |

To serve the UI from the vite dev server, set this before launching Rhino (while `make ui-dev` is running):

```bash
export CORHINO_WEBUI_URL=http://localhost:5173
```

The panel then loads the dev server URL instead of the on-disk `web/index.html`.

### Project structure

```
src/            C# Rhino plugin
├── Panel/      panel orchestrator, drawers, orchestration protocol, workspace/settings persistence
├── Cli/        CLI adapter layer (IAgentCli / ClaudeCli / CodexCli — args, isolation, ID discovery)
├── Pty/        PTY sessions (Porta.Pty) + CLI resolution
└── Commands/   the "CoRhino" command (opens the panel)
cc-plugin/      bundled Claude Code plugin (save-rhino-skill / orchestrate-agents)
ui/             panel UI (TypeScript + xterm.js, vite)
yak/            package manifest.yml + icon
build-yak.sh    assembles the .yak package
Makefile        main commands (make help)
```

### Packaging (yak)

The `.yak` package bundles the build output (`.rhp` + all dependencies) — the same file set as a local working install.

```bash
make pack                 # Release build → dist/yak/corhino-<ver>-rh8_32-<os>.yak
make pack VERSION=0.1.0   # pin the package version
```

(`make pack` just calls `build-yak.sh`; running the script directly is equivalent.)

- Packages are **per-OS** (the native PTY library is OS-specific): build the mac package on macOS and the win package on Windows (Git Bash / WSL).
- Output goes to `dist/yak/` (gitignored); manifest and icon live in `yak/`.
- Manual publish: `make yak-login` → `make yak-push-test` (test) → `make yak-push` (production).

### Release automation (GitHub Actions)

Normal releases are handled by CI ([.github/workflows/release.yml](.github/workflows/release.yml)).

- **Push a `v*` tag** → macos / windows runners build both `.yak` packages and **push to the production server**. The job fails if the tag doesn't match the csproj `<Version>` (the csproj stays the single source of truth).
- **workflow_dispatch (manual run)** → the same build goes to the **test server** (wiped nightly), for rehearsing the release pipeline.

```bash
# release: bump <Version> in the csproj, commit, then push a tag
git tag v0.2.0 && git push origin v0.2.0
```

How it fits together:

- Platform tags (`-mac`/`-win`) are **baked into the package** by `yak build --platform`, so pushing collapses into a single linux job (only the builds need per-OS runners).
- The [standalone yak CLI](https://files.mcneel.com/yak/tools/0.13.0/) is used, so runners don't need Rhino installed. Compile references (RhinoCommon / Rhino.UI / Eto) come from the RhinoCommon NuGet package.
- **One-time setup**: run `yak login --ci` locally to get a **non-expiring** API key and store it as the `YAK_TOKEN` repo secret (yak reads `YAK_TOKEN` automatically). Regular `yak login` tokens expire after ~30 days — don't use them in CI.

### Minimum supported Rhino (distribution tag)

yak stamps the **RhinoCommon version** the `.rhp` references as the distribution tag (e.g. `rh8_32` = Rhino 8.32+). To keep the floor from silently drifting with the dev machine's installed Rhino, `RhinoCommonVersion` in `src/CoRhino.csproj` **pins the NuGet RhinoCommon** (`ExcludeAssets="runtime"` = compile-only; the real assemblies come from Rhino at runtime).

- Current floor: **rh8_32** (`8.32.26160.13001`, latest).
- Lowest pinnable: **rh8_19** (`8.19.25132.1001`) — the RhinoCommon NuGet only ships CoreCLR (net7.0) assemblies from 8.19 on; 8.18 and earlier are net48-only and won't restore in a net8.0 build.
- To support older Rhino 8, lower `RhinoCommonVersion` (floor: 8.19).

## License

[MIT](LICENSE). Bundled third-party components (Porta.Pty, xterm.js — both MIT) are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); both files ship inside the `.yak` package.
