using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CoRhino.Panel;

namespace CoRhino.Cli
{
    /// <summary>
    /// The pi coding-agent CLI adapter (@earendil-works/pi-coding-agent).
    /// Isolation stance: pi only reads MCP servers from &lt;agentDir&gt;/mcp.json
    /// and a trusted project's .pi/mcp.json — there is no --mcp-config flag.
    /// So each spawn gets its OWN agent dir (PI_CODING_AGENT_DIR) under
    /// ~/.corhino/runtime: the user's model/auth/settings files are linked in
    /// (so their providers — e.g. google-vertex — keep working and token
    /// refreshes land in the real files), while mcp.json is generated with
    /// ONLY this panel's rhino server. User extensions/skills under ~/.pi/agent
    /// are therefore not loaded — the same stance as codex --disable plugins.
    /// --no-approve keeps project-local .pi resources out as well. Sessions are
    /// stored under a CoRhino-owned --session-dir and the id is pinned at spawn
    /// (--session-id), like claude.
    /// </summary>
    internal sealed class PiCli : IAgentCli
    {
        /// <summary>The model every pi tab starts on (switchable in-session with Ctrl+P / /model).</summary>
        public const string DefaultModel = "google-vertex/gemini-4-carbon";

        public AgentCliKind Kind => AgentCliKind.Pi;
        public string DisplayName => "pi";
        public string BinaryName => "pi";
        public string NativeInstallCommand => "npm install -g @earendil-works/pi-coding-agent";
        public IReadOnlyList<string> WellKnownBinaryPaths =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                ? new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "npm", "pi.cmd"),
                }
                : new[] { "/opt/homebrew/bin/pi", "/usr/local/bin/pi" };

        // Skills ride in via --skill flags (user store + bundled cc-plugin).
        // No in-place reload wiring: changes reach new tabs.
        public bool SupportsSkillsDrawer => true;
        public bool SupportsSkillsReload => false;
        public bool LoadsSkillsViaWorkspaceMount => false;

        // Same derivation as claude: the id is pinned at spawn, so the layer is
        // a pure function of it and survives resume without an index.
        public string? LayerTokenFor(string? sessionId) =>
            sessionId == null ? null : "s-" + sessionId.Substring(0, Math.Min(8, sessionId.Length));

        static string UserPiAgentDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");

        static string CoRhinoPiRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".corhino", "pi");

        /// <summary>Where CoRhino's pi transcripts live (stable across spawns, so resume works).</summary>
        public static string SessionDir => Path.Combine(CoRhinoPiRoot, "sessions");

        // Files carried over from the user's pi config. Linked (not copied)
        // so OAuth/token refreshes and model-catalog updates write through.
        static readonly string[] SharedFiles =
            { "models.json", "models-store.json", "auth.json", "settings.json", "trust.json" };

        public IReadOnlyDictionary<string, string> BuildEnv(SpawnContext ctx)
        {
            var env = new Dictionary<string, string>(ReadProviderEnv())
            {
                ["PI_CODING_AGENT_DIR"] = EnsureAgentDir(ctx.HostPort, ctx.AgentId),
            };
            return env;
        }

        // Provider settings pi reads from the environment (Vertex ADC project /
        // location, API keys). Users typically export them in ~/.zshrc, which
        // a Dock-launched Rhino never sources (and CliResolver's bash -lc
        // doesn't either) — so a pi tab would fail with "No API key found".
        // Pull ONLY these names from the user's interactive login shell.
        // Values go straight into the child env and are never logged.
        static readonly string[] ProviderEnvNames =
        {
            "GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_LOCATION", "VERTEX_LOCATION",
            "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_CLOUD_API_KEY", "CLOUDSDK_CONFIG",
        };

        static Dictionary<string, string> ReadProviderEnv()
        {
            var result = new Dictionary<string, string>();
            foreach (var n in ProviderEnvNames)
            {
                var v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrEmpty(v)) result[n] = v!;
            }
            if (OperatingSystem.IsWindows() || result.Count == ProviderEnvNames.Length) return result;

            try
            {
                var shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "/bin/zsh";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = shell,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "-ilc", "env -0" }) psi.ArgumentList.Add(a);
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return result;
                proc.StandardInput.Close();
                var readTask = proc.StandardOutput.ReadToEndAsync();
                if (!readTask.Wait(5000)) { try { proc.Kill(true); } catch { } return result; }
                foreach (var entry in readTask.Result.Split('\0'))
                {
                    var eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    // -i may prefix stdout with rc noise; the name is the tail after the last newline.
                    var name = entry.Substring(0, eq);
                    var nl = name.LastIndexOf('\n');
                    if (nl >= 0) name = name.Substring(nl + 1);
                    if (Array.IndexOf(ProviderEnvNames, name) < 0 || result.ContainsKey(name)) continue;
                    var value = entry.Substring(eq + 1);
                    if (value.Length > 0) result[name] = value;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] pi: could not read provider env from login shell: {ex.GetType().Name}");
            }
            Log.Write($"[CoRhino] pi: provider env forwarded: {string.Join(", ", result.Keys)}");
            return result;
        }

        public IReadOnlyList<string> BuildArgs(SpawnContext ctx)
        {
            Directory.CreateDirectory(SessionDir);
            var args = new List<string>
            {
                "--model", DefaultModel,
                "--session-dir", SessionDir,
                // Project-local .pi resources (extensions, mcp.json) never load
                // in the CoRhino workspace — opening the panel is not consent to them.
                "--no-approve",
            };

            if (ctx.SessionId != null)
            {
                // --session-id resumes the id when it exists and creates it otherwise.
                args.Add("--session-id");
                args.Add(ctx.SessionId);
                args.Add("--append-system-prompt");
                args.Add(L10n.T(
                    $"このセッションの担当 Rhino レイヤは `{ctx.LayerToken}` です。" +
                    "オブジェクトの作成・変更・削除はこのレイヤ配下のみで行ってください" +
                    "（詳細は AGENTS.md / CLAUDE.md の「並列エージェントのルール」）。" +
                    $"\nあなたの agentId は `{ctx.AgentId}`、パネルポートは `{ctx.HostPort}` です。",
                    $"This session's assigned Rhino layer is `{ctx.LayerToken}`. " +
                    "Create, modify, and delete objects only under this layer " +
                    "(see the parallel-agent rules in AGENTS.md / CLAUDE.md)." +
                    $"\nYour agentId is `{ctx.AgentId}` and your panel port is `{ctx.HostPort}`."));
            }

            var userSkills = Path.Combine(Workspace.EnsureUserSkillsPlugin(), "skills");
            if (Directory.Exists(userSkills))
            {
                args.Add("--skill");
                args.Add(userSkills);
            }
            var bundledSkills = Path.Combine(
                Path.GetDirectoryName(typeof(PiCli).Assembly.Location) ?? string.Empty,
                "cc-plugin", "skills");
            if (Directory.Exists(bundledSkills))
            {
                args.Add("--skill");
                args.Add(bundledSkills);
            }

            return args;
        }

        public Task<string?> DiscoverSessionIdAsync(SpawnContext ctx, CancellationToken ct) =>
            Task.FromResult(ctx.SessionId);

        /// <summary>
        /// Build ~/.corhino/runtime/pi-agent-&lt;port&gt;-&lt;agentId&gt;/: the user's
        /// shared config files linked in, plus an mcp.json with only the rhino
        /// server (same shape and extras as claude's per-agent config).
        /// </summary>
        static string EnsureAgentDir(int port, string agentId)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".corhino", "runtime", $"pi-agent-{port}-{agentId}");
            Directory.CreateDirectory(dir);

            foreach (var name in SharedFiles)
            {
                var src = Path.Combine(UserPiAgentDir, name);
                var dst = Path.Combine(dir, name);
                try
                {
                    if (File.Exists(dst) || new FileInfo(dst).LinkTarget != null) File.Delete(dst);
                    if (!File.Exists(src)) continue;
                    try { File.CreateSymbolicLink(dst, src); }
                    catch (Exception) { File.Copy(src, dst, overwrite: true); } // e.g. Windows without symlink privilege
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] pi agent dir: could not link {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Reuse claude's per-agent config (rhino + extraMcpServers) — the
            // mcpServers JSON shape is the same in pi.
            var claudeMcp = Workspace.EnsureAgentMcpConfig(port, agentId);
            var config = JsonNode.Parse(File.ReadAllText(claudeMcp))!;
            File.WriteAllText(Path.Combine(dir, "mcp.json"),
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return dir;
        }
    }
}
