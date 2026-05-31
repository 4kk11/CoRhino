using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CoRhino.Pty
{
    internal static class CliResolver
    {
        public static (string? clausePath, string? loginPath) Resolve()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ResolveWindows();
            }
            return ResolveUnix();
        }

        static (string?, string?) ResolveUnix()
        {
            string? claudePath = RunLoginShell("command -v claude || which claude");
            string? loginPath = RunLoginShell("echo -n \"$PATH\"");
            return (claudePath, loginPath);
        }

        static (string?, string?) ResolveWindows()
        {
            string? claudePath = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "claude",
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
                    claudePath =
                        lines.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? lines.FirstOrDefault(l => l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                        ?? lines.FirstOrDefault(l => l.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                        ?? lines.FirstOrDefault();

                    // npm/yarn-style .cmd shims usually just forward to a .exe sibling.
                    // Resolving the .exe lets us bypass cmd.exe — going through cmd.exe interferes with
                    // ConPTY passthrough of ANSI control sequences, breaking TUIs like Ink that rely on
                    // efficient cursor-positioned redraws (claude flickers/duplicates banner without this).
                    if (claudePath != null && claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        var direct = ResolveCmdShimTarget(claudePath);
                        if (direct != null) claudePath = direct;
                    }
                }
            }
            catch { }

            var loginPath = Environment.GetEnvironmentVariable("PATH");
            return (claudePath, loginPath);
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
