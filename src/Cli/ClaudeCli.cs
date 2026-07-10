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
                // Status hooks (busy/waiting/idle → this agent's status file;
                // the panel watches the file to drive the tab indicator) plus
                // the sandbox block when the panel setting is on.
                "--settings", Workspace.EnsureClaudeStatusSettings(ctx.HostPort, ctx.AgentId, ctx.SandboxEnabled),
            };

            // An orchestrating parent may relax the child's approval flow
            // (design §1.3); vocabulary was validated at the command boundary.
            // User-created tabs never carry a mode — claude's default rules.
            if (ctx.PermissionMode != null)
            {
                args.Add("--permission-mode");
                args.Add(ctx.PermissionMode);
            }

            if (ctx.SessionId != null)
            {
                // Pin the session identity: fresh tabs get their pre-generated
                // GUID (--session-id) so the tab↔transcript mapping is known
                // without parsing anything; resumed tabs reattach to the
                // existing id (--resume, no --fork-session — the same jsonl
                // keeps growing).
                args.Add(ctx.Resume ? "--resume" : "--session-id");
                args.Add(ctx.SessionId);
                // Per-session values the shared workspace CLAUDE.md can't carry:
                // the parallel-agent layer (see LayerTokenFor) and the agent's
                // own identity/paths for the orchestration protocol — without
                // these a would-be orchestrator has to reverse-engineer who it
                // is (the reflection experiment's first stumbling block).
                args.Add("--append-system-prompt");
                args.Add(L10n.T(
                    $"このセッションの担当 Rhino レイヤは `{ctx.LayerToken}` です。" +
                    "オブジェクトの作成・変更・削除はこのレイヤ配下のみで行ってください" +
                    "（詳細は CLAUDE.md の「並列エージェントのルール」）。" +
                    $"\nあなたの agentId は `{ctx.AgentId}`、パネルポートは `{ctx.HostPort}` です。" +
                    $"エージェントオーケストレーション（子エージェントのタブ作成・指示・回収）のコマンド置き場は " +
                    $"`{Workspace.CommandsDir}`、タブ一覧マニフェストは `{Workspace.TabsManifestPath(ctx.HostPort)}` です。" +
                    "使い方は orchestrate-agents スキルを参照してください。",
                    $"This session's assigned Rhino layer is `{ctx.LayerToken}`. " +
                    "Create, modify, and delete objects only under this layer " +
                    "(see the parallel-agent rules in CLAUDE.md)." +
                    $"\nYour agentId is `{ctx.AgentId}` and your panel port is `{ctx.HostPort}`. " +
                    $"The agent-orchestration command drop-box is `{Workspace.CommandsDir}` and the " +
                    $"live-tab manifest is `{Workspace.TabsManifestPath(ctx.HostPort)}`; " +
                    "see the orchestrate-agents skill for usage."));
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
