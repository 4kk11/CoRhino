using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace RhinoClaudePanel.Pty
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
                    claudePath = stdout
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => !string.IsNullOrEmpty(l));
                }
            }
            catch { }

            var loginPath = Environment.GetEnvironmentVariable("PATH");
            return (claudePath, loginPath);
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
