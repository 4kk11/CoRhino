using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CoRhino.Pty
{
    /// <summary>
    /// Resolves an agent CLI binary ("claude", "codex", …) to something
    /// spawnable, plus the login shell's PATH. Resolved fresh on every call —
    /// spawns always see the current PATH state (a CLI installed or moved while
    /// Rhino runs is picked up by the next spawn). Callers needing repeated
    /// availability info (the tab-bar's CLI menu) probe once on a background
    /// thread and hold the result themselves.
    ///
    /// The result is an app path PLUS prefix args, not just a path: on Windows
    /// an npm .cmd shim may resolve to `node.exe &lt;pkg&gt;/bin/tool.js`, which no
    /// single path can express. Callers must prepend <c>prefixArgs</c> to the
    /// CLI's own args wherever they spawn <c>cliPath</c>.
    /// </summary>
    internal static class CliResolver
    {
        public static (string? cliPath, string[] prefixArgs, string? loginPath) Resolve(string binaryName)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ResolveWindows(binaryName)
                : ResolveUnix(binaryName);
        }

        static (string?, string[], string?) ResolveUnix(string binaryName)
        {
            string? cliPath = RunLoginShell($"command -v {binaryName} || which {binaryName}");
            string? loginPath = RunLoginShell("echo -n \"$PATH\"");
            // The official native installers (claude.ai/install.sh,
            // chatgpt.com/codex/install.sh) drop the binary into ~/.local/bin but
            // write their PATH line to the USER's shell profile — .zprofile/.zshrc
            // on the default macOS zsh — which the bash -lc probe above never
            // reads. Without this direct check a just-completed native install
            // (the setup view's whole point) would still probe as missing.
            cliPath ??= WellKnownBinary(binaryName);
            return (cliPath, Array.Empty<string>(), loginPath);
        }

        // ~/.local/bin/<name> — where both CLIs' native installers put the binary.
        static string? WellKnownBinary(string binaryName)
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin",
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? binaryName + ".exe" : binaryName);
            return File.Exists(candidate) ? candidate : null;
        }

        static (string?, string[], string?) ResolveWindows(string binaryName)
        {
            string? cliPath = null;
            var prefixArgs = Array.Empty<string>();
            var lines = WhereExe(binaryName);

            // Prefer paths that CreateProcess can launch directly (.exe).
            // Then .cmd/.bat (must be wrapped via cmd.exe by the caller).
            // Avoid extensionless shell-shim scripts (e.g. fnm/npm sh shims) — they fail with "not a valid Win32 application".
            cliPath =
                lines.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault(l => l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault(l => l.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault();

            // npm/yarn-style .cmd shims forward to a .exe sibling or to
            // `node.exe <script.js>`. Resolving the real target lets us bypass
            // cmd.exe — going through cmd.exe interferes with ConPTY passthrough
            // of ANSI control sequences, breaking TUIs like Ink that rely on
            // efficient cursor-positioned redraws (claude flickers/duplicates
            // banner without this), and its `/c` re-parsing mangles args that
            // contain quotes (codex's TOML -c overrides).
            if (cliPath != null && cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                var direct = ResolveCmdShim(cliPath);
                if (direct != null) (cliPath, prefixArgs) = direct.Value;
            }

            // Same well-known-dir fallback as unix: the native installers use
            // %USERPROFILE%\.local\bin on Windows, which a fresh install may not
            // have on the process PATH yet.
            cliPath ??= WellKnownBinary(binaryName);

            var loginPath = Environment.GetEnvironmentVariable("PATH");
            return (cliPath, prefixArgs, loginPath);
        }

        static List<string> WhereExe(string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return new List<string>();
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                return stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Resolve an npm-style .cmd shim to what it actually runs. Two shapes
        /// exist in the wild:
        ///
        ///   direct-exe:  "%dp0%\node_modules\&lt;pkg&gt;\bin\tool.exe" %*      (claude)
        ///   node-script: IF EXIST "%dp0%\node.exe" (SET "_prog=%dp0%\node.exe") …
        ///                "%_prog%" "%dp0%\node_modules\&lt;pkg&gt;\bin\tool.js" %*   (codex)
        ///
        /// The direct-exe shape resolves to the exe itself. The node-script shape
        /// resolves to node.exe + the script as a prefix arg — node.exe alone
        /// must NEVER be the answer: node.exe is quoted in the shim (that IF
        /// EXIST probe) but launching it bare drops the script and hands the
        /// tool's args to a naked node, which exits instantly (this is exactly
        /// how codex tabs died on Windows). Null when neither shape matches —
        /// the caller keeps the .cmd and wraps it via cmd.exe.
        /// </summary>
        static (string app, string[] prefixArgs)? ResolveCmdShim(string cmdPath)
        {
            try
            {
                var content = File.ReadAllText(cmdPath);

                // direct-exe shape: the first quoted .exe that is not node.exe.
                foreach (Match m in Regex.Matches(content, "\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase))
                {
                    var exe = ExpandDp0(m.Groups[1].Value, cmdPath);
                    if (Path.GetFileName(exe).Equals("node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(exe)) return (exe, Array.Empty<string>());
                }

                // node-script shape: the first quoted node script.
                var js = Regex.Match(content, "\"([^\"]+\\.[cm]?js)\"", RegexOptions.IgnoreCase);
                if (js.Success)
                {
                    var script = ExpandDp0(js.Groups[1].Value, cmdPath);
                    if (File.Exists(script))
                    {
                        var node = FindNodeFor(cmdPath);
                        if (node != null) return (node, new[] { script });
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // The node.exe a shim's script should run under: the shim's own dir
        // first (fnm/volta co-locate node.exe with the shims — and it's the
        // version the user aliased), then PATH.
        static string? FindNodeFor(string cmdPath)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(cmdPath) ?? "", "node.exe");
            if (File.Exists(sibling)) return sibling;
            return WhereExe("node")
                .FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        // Expand the shim's %~dp0 / %dp0% (the shim's own directory) in a quoted
        // path and normalize doubled backslashes.
        static string ExpandDp0(string raw, string cmdPath)
        {
            var dp0 = Path.GetDirectoryName(cmdPath) ?? "";
            if (!dp0.EndsWith("\\")) dp0 += "\\";
            var expanded = raw
                .Replace("%~dp0", dp0, StringComparison.OrdinalIgnoreCase)
                .Replace("%dp0%", dp0, StringComparison.OrdinalIgnoreCase);
            while (expanded.Contains("\\\\")) expanded = expanded.Replace("\\\\", "\\");
            return expanded;
        }

        static string? RunLoginShell(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-lc \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                return string.IsNullOrEmpty(stdout) ? null : stdout;
            }
            catch
            {
                return null;
            }
        }
    }
}
