using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CoRhino.Pty
{
    /// <summary>
    /// Resolves an agent CLI binary ("claude", "codex", …) to an absolute path,
    /// plus the login shell's PATH. Resolved fresh on every call — spawns always
    /// see the current PATH state (a CLI installed or moved while Rhino runs is
    /// picked up by the next spawn). Callers needing repeated availability info
    /// (the tab-bar's CLI menu) probe once on a background thread and hold the
    /// result themselves.
    /// </summary>
    internal static class CliResolver
    {
        public static (string? cliPath, string? loginPath) Resolve(string binaryName)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ResolveWindows(binaryName)
                : ResolveUnix(binaryName);
        }

        static (string?, string?) ResolveUnix(string binaryName)
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
            return (cliPath, loginPath);
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

        static (string?, string?) ResolveWindows(string binaryName)
        {
            string? cliPath = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = binaryName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    var lines = stdout
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

                    // Prefer paths that CreateProcess can launch directly (.exe).
                    // Then .cmd/.bat (must be wrapped via cmd.exe by the caller).
                    // Avoid extensionless shell-shim scripts (e.g. fnm/npm sh shims) — they fail with "not a valid Win32 application".
                    cliPath =
                        lines.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? lines.FirstOrDefault(l => l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                        ?? lines.FirstOrDefault(l => l.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                        ?? lines.FirstOrDefault();

                    // npm/yarn-style .cmd shims usually just forward to a .exe sibling.
                    // Resolving the .exe lets us bypass cmd.exe — going through cmd.exe interferes with
                    // ConPTY passthrough of ANSI control sequences, breaking TUIs like Ink that rely on
                    // efficient cursor-positioned redraws (claude flickers/duplicates banner without this).
                    if (cliPath != null && cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        var direct = ResolveCmdShimTarget(cliPath);
                        if (direct != null) cliPath = direct;
                    }
                }
            }
            catch { }

            // Same well-known-dir fallback as unix: the native installers use
            // %USERPROFILE%\.local\bin on Windows, which a fresh install may not
            // have on the process PATH yet.
            cliPath ??= WellKnownBinary(binaryName);

            var loginPath = Environment.GetEnvironmentVariable("PATH");
            return (cliPath, loginPath);
        }

        static string? ResolveCmdShimTarget(string cmdPath)
        {
            try
            {
                var content = File.ReadAllText(cmdPath);
                // Match the first quoted .exe path in the shim file.
                // npm shim shape: "%dp0%\node_modules\...\foo.exe"  or  "%~dp0\node.exe"
                var match = Regex.Match(content, "\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
                if (!match.Success) return null;

                var raw = match.Groups[1].Value;
                var dp0 = Path.GetDirectoryName(cmdPath) ?? "";
                if (!dp0.EndsWith("\\")) dp0 += "\\";

                var expanded = raw
                    .Replace("%~dp0", dp0, StringComparison.OrdinalIgnoreCase)
                    .Replace("%dp0%", dp0, StringComparison.OrdinalIgnoreCase);
                while (expanded.Contains("\\\\")) expanded = expanded.Replace("\\\\", "\\");

                return File.Exists(expanded) ? expanded : null;
            }
            catch
            {
                return null;
            }
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
