using System;
using System.Diagnostics;

namespace RhinoClaudePanel.Pty
{
    internal static class CliResolver
    {
        public static (string? clausePath, string? loginPath) Resolve()
        {
            string? claudePath = RunLoginShell("command -v claude || which claude");
            string? loginPath = RunLoginShell("echo -n \"$PATH\"");
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
