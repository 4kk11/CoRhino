using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoRhino.Panel;

namespace CoRhino.Cli
{
    /// <summary>
    /// The claude CLI adapter. Isolation stance: --strict-mcp-config plus a
    /// per-agent --mcp-config file, so the session sees ONLY this panel's rhino
    /// server and nothing is written to ~/.claude. The user's personal skills
    /// and the bundled onboarding plugin ride along via --plugin-dir.
    /// </summary>
    internal sealed class ClaudeCli : IAgentCli
    {
        public AgentCliKind Kind => AgentCliKind.Claude;
        public string DisplayName => "Claude Code";
        public string BinaryName => "claude";
        // The official native installer (first-choice in Anthropic's docs; the
        // only channel with background auto-updates). Lands in ~/.local/bin.
        public string NativeInstallCommand =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? "irm https://claude.ai/install.ps1 | iex"
                : "curl -fsSL https://claude.ai/install.sh | bash";
        // ~/.local/bin on every OS — the native installer's only target.
        public IReadOnlyList<string> WellKnownBinaryPaths => new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin",
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                    ? "claude.exe" : "claude"),
        };
        public bool SupportsSkillsDrawer => true;
        public bool SupportsSkillsReload => true;          // /plugin reload-plugins
        public bool LoadsSkillsViaWorkspaceMount => false; // --plugin-dir ×2 (user + cc-plugin)

        // Derived deterministically from the sessionId so a resumed session gets
        // the SAME layer it created objects in — no {sessionId → layer} index to
        // persist. Injected per-spawn via --append-system-prompt (the shared
        // workspace CLAUDE.md can't carry a per-agent value).
        public string? LayerTokenFor(string? sessionId) =>
            sessionId == null ? null : "s-" + sessionId.Substring(0, Math.Min(8, sessionId.Length));

        public IReadOnlyList<string> BuildArgs(SpawnContext ctx)
        {
            // Load ONLY this agent's RhinoMCP config (which points at this
            // instance's host port and carries this agent's header) and ignore
            // the user's global/project MCP settings, so claude talks to this
            // Rhino and nothing leaks in. The user's personal skills ride along
            // as a local plugin — same stance: delivered per session, nothing
            // written to ~/.claude.
            var args = new List<string>
            {
                "--strict-mcp-config",
                "--mcp-config", Workspace.EnsureAgentMcpConfig(ctx.HostPort, ctx.AgentId),
                "--plugin-dir", Workspace.EnsureUserSkillsPlugin(),
            };

            if (ctx.SessionId != null)
            {
                // Pin the session identity: fresh tabs get their pre-generated
                // GUID (--session-id) so the tab↔transcript mapping is known
                // without parsing anything; resumed tabs reattach to the
                // existing id (--resume, no --fork-session — the same jsonl
                // keeps growing).
                args.Add(ctx.Resume ? "--resume" : "--session-id");
                args.Add(ctx.SessionId);
                // The parallel-agent layer, session-bound (see LayerTokenFor).
                // The shared workspace CLAUDE.md holds the rules; this carries
                // the one per-session value it can't.
                args.Add("--append-system-prompt");
                args.Add(L10n.T(
                    $"このセッションの担当 Rhino レイヤは `{ctx.LayerToken}` です。" +
                    "オブジェクトの作成・変更・削除はこのレイヤ配下のみで行ってください" +
                    "（詳細は CLAUDE.md の「並列エージェントのルール」）。",
                    $"This session's assigned Rhino layer is `{ctx.LayerToken}`. " +
                    "Create, modify, and delete objects only under this layer " +
                    "(see the parallel-agent rules in CLAUDE.md)."));
            }

            // CoRhino's bundled cc-plugin (onboarding skills like save-rhino-skill)
            // ships next to the assembly, like web/. Skip silently if a stripped
            // install lacks it — the session works without onboarding skills.
            var bundledPlugin = Path.Combine(
                Path.GetDirectoryName(typeof(ClaudeCli).Assembly.Location) ?? string.Empty,
                "cc-plugin");
            if (Directory.Exists(bundledPlugin))
            {
                args.Add("--plugin-dir");
                args.Add(bundledPlugin);
            }

            return args;
        }

        // Claude's identity is fixed at spawn time (--session-id / --resume);
        // there is nothing to discover.
        public Task<string?> DiscoverSessionIdAsync(SpawnContext ctx, CancellationToken ct) =>
            Task.FromResult(ctx.SessionId);
    }
}
