using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoRhino.Cli
{
    /// <summary>
    /// Everything CoRhinoPanel needs to know to spawn one agent process. Built
    /// on the UI thread at spawn time and handed to the adapter — adapters never
    /// reach back into panel state.
    /// </summary>
    /// <param name="AgentId">The tab's routing key (agent-N), also sent as X-CoRhino-Agent.</param>
    /// <param name="HostPort">This panel's RhinoMCP host port.</param>
    /// <param name="Cwd">The shared workspace dir every session runs in.</param>
    /// <param name="SessionId">Claude: pre-generated GUID (fresh) or the resumed id.
    /// Codex: null on fresh spawns (the id is discovered after launch), the known id on resume.</param>
    /// <param name="Resume">True when reattaching to an existing session.</param>
    /// <param name="LayerToken">The parallel-agent Rhino layer (s-XXXXXXXX) this
    /// session must confine its writes to. Null only when no session identity exists.</param>
    /// <param name="SpawnedAtUtc">Just before process launch; anchors codex rollout discovery.</param>
    internal sealed record SpawnContext(
        string AgentId,
        int HostPort,
        string Cwd,
        string? SessionId,
        bool Resume,
        string? LayerToken,
        DateTime SpawnedAtUtc);

    /// <summary>
    /// Adapter for one agent CLI (claude, codex, …). Owns every per-CLI concern
    /// the panel would otherwise branch on: launch arguments (including MCP
    /// isolation and layer-instruction injection), session identity strategy,
    /// and feature availability. PTY plumbing, tab management, and WebView
    /// wiring stay in CoRhinoPanel — they are CLI-independent.
    /// </summary>
    internal interface IAgentCli
    {
        AgentCliKind Kind { get; }

        /// <summary>Shown in the tab-bar "+" menu ("Claude Code", "Codex").</summary>
        string DisplayName { get; }

        /// <summary>The executable to resolve on PATH ("claude", "codex").</summary>
        string BinaryName { get; }

        /// <summary>
        /// The CLI's official native-installer one-liner for the current platform.
        /// The setup view shows it verbatim AND its install button runs exactly
        /// this string (unix: bash -lc, windows: powershell -Command) — display
        /// and execution must never diverge.
        /// </summary>
        string NativeInstallCommand { get; }

        /// <summary>
        /// The tab's layer token for a given session identity. Claude derives it
        /// deterministically from the session id (null in → null out). Codex:
        /// null mints a FRESH token (the id doesn't exist yet at spawn; call
        /// once per spawn and keep the result on the tab), a known id restores
        /// its persisted token via SessionLayers (minting and persisting one on
        /// a miss).
        /// </summary>
        string? LayerTokenFor(string? sessionId);

        /// <summary>
        /// Build the full argument list for a spawn. May prepare the files it
        /// needs (e.g. claude's per-agent .mcp.json) — args must be ready to
        /// hand to the PTY as-is.
        /// </summary>
        IReadOnlyList<string> BuildArgs(SpawnContext ctx);

        /// <summary>
        /// Resolve the session id after the process launched. Claude knows it
        /// up front (returns ctx.SessionId immediately); codex polls its rollout
        /// store and may return null if discovery fails (the tab stays usable,
        /// it just can't be resumed later). Runs on a background thread.
        /// </summary>
        Task<string?> DiscoverSessionIdAsync(SpawnContext ctx, CancellationToken ct);

        /// <summary>Whether the Skills Drawer works for this CLI's tabs.</summary>
        bool SupportsSkillsDrawer { get; }

        /// <summary>
        /// Whether a running session can re-load skills in place (claude's
        /// /plugin reload-plugins). False → skills changes reach new tabs only,
        /// and the drawer hides its ⟳.
        /// </summary>
        bool SupportsSkillsReload { get; }

        /// <summary>
        /// Whether skills reach this CLI's sessions through the workspace
        /// symlinks (codex: .agents/skills + .codex/skills) instead of launch
        /// flags (claude: --plugin-dir). Mounted skills are only listed while
        /// their link is verifiably in place — the drawer must not show skills
        /// the session doesn't actually have (see SendSkillsList).
        /// </summary>
        bool LoadsSkillsViaWorkspaceMount { get; }
    }
}
