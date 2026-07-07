using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Eto.Forms;
using Rhino;
using Rhino.PlugIns;

namespace CoRhino.Panel
{
    /// <summary>
    /// Phase 2a onboarding: make sure the official Rhino-MCP-Platform yak package is
    /// present so the panel's direct-HTTP wiring has a host to start. A non-engineer
    /// must never type <c>yak install</c> or paste config; the panel detects the
    /// missing plugin, asks once for consent, runs <c>yak install</c> itself, then
    /// tells the user to restart Rhino (yak-installed plugins only load on startup).
    /// </summary>
    static class RhinoMcpInstaller
    {
        // Rhino-MCP-Platform plug-in id, from RhinoMCP/rhino/plugin/Properties/AssemblyInfo.cs.
        // PlugInExists reports it as soon as Rhino has *registered* the package — true
        // for a yak install or a local Debug copy, and the reason we detect by id
        // rather than by scanning the yak package dir (a local build is not a yak pkg).
        static readonly Guid PluginId = new("2668d7ed-f507-4a68-8295-8172147a0e39");

        const string PackageName = "Rhino-MCP-Platform";
        const string DialogCaption = "CoRhino";

        /// <summary>True once Rhino knows the plugin (installed; load may be pending).</summary>
        public static bool IsInstalled() => PlugIn.PlugInExists(PluginId, out _, out _);

        /// <summary>
        /// Drive the install onboarding. Safe to call from the panel constructor: the
        /// consent dialog and yak run are deferred to the next <see cref="RhinoApp.Idle"/>
        /// so no modal blocks panel construction. The actual install runs on a
        /// background thread; result/restart/error are reported via native dialogs.
        /// </summary>
        public static void RunOnboarding()
        {
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                RhinoApp.Idle -= handler;
                try { StartOnboarding(); }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] Onboarding threw: {ex.GetType().Name}: {ex.Message}");
                }
            };
            RhinoApp.Idle += handler;
        }

        static void StartOnboarding()
        {
            var yak = ResolveYak();
            if (yak == null)
            {
                Log.Write("[CoRhino] yak CLI not found; cannot auto-install Rhino-MCP-Platform.");
                ShowError(L10n.T(
                    "Rhino 操作に必要な Rhino-MCP-Platform が未導入ですが、yak CLI が見つからず自動インストールできませんでした。\n\n" +
                    "Rhino の PackageManager から \"Rhino-MCP-Platform\" を手動でインストールし、Rhino を再起動してください。",
                    "Rhino-MCP-Platform (required for operating Rhino) is not installed, and the yak CLI could not be found for automatic installation.\n\n" +
                    "Please install \"Rhino-MCP-Platform\" manually from Rhino's PackageManager and restart Rhino."));
                return;
            }

            var consent = MessageBox.Show(
                L10n.T(
                    "Rhino を Claude で操作するには Rhino-MCP-Platform が必要です。\n\n" +
                    "今すぐインストールしますか？（完了後に Rhino の再起動が必要です）",
                    "Operating Rhino with Claude requires Rhino-MCP-Platform.\n\n" +
                    "Install it now? (Rhino must be restarted afterwards)"),
                DialogCaption,
                MessageBoxButtons.YesNo,
                MessageBoxType.Question);

            if (consent != DialogResult.Yes)
            {
                Log.Write("[CoRhino] User declined Rhino-MCP-Platform install.");
                return;
            }

            // Lightweight progress on the command line; the heavy states (done→restart,
            // error) are native dialogs per the chosen status mechanism.
            Log.Write($"[CoRhino] Installing {PackageName} via {yak} ...");
            RhinoApp.WriteLine(L10n.T($"{PackageName} をインストールしています...", $"Installing {PackageName}..."));

            Task.Run(() =>
            {
                var (ok, output) = RunYakInstall(yak);
                Application.Instance?.AsyncInvoke(() =>
                {
                    if (ok)
                    {
                        ShowInfo(L10n.T(
                            $"{PackageName} のインストールが完了しました。\n\n" +
                            "Rhino を再起動すると、このパネルから Rhino を操作できるようになります。",
                            $"{PackageName} was installed successfully.\n\n" +
                            "Restart Rhino to start operating it from this panel."));
                    }
                    else
                    {
                        ShowError(L10n.T(
                            $"{PackageName} のインストールに失敗しました。\n\n",
                            $"Failed to install {PackageName}.\n\n") +
                            (string.IsNullOrEmpty(output) ? L10n.T("詳細はログを確認してください。", "See the log for details.") : output));
                    }
                });
            });
        }

        static (bool ok, string output) RunYakInstall(string yak)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = yak,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add(PackageName);

                using var proc = Process.Start(psi);
                if (proc == null) return (false, L10n.T("yak プロセスを起動できませんでした。", "Could not start the yak process."));

                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(120000))
                {
                    try { proc.Kill(true); } catch { }
                    return (false, L10n.T("yak install がタイムアウトしました（120s）。", "yak install timed out (120s)."));
                }

                var combined = ($"{stdout}\n{stderr}").Trim();
                Log.Write($"[CoRhino] yak install exit={proc.ExitCode}: {combined}");
                return (proc.ExitCode == 0, combined);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] yak install threw: {ex.GetType().Name}: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Locate the yak CLI shipped with the running Rhino. macOS: it lives in the
        /// app bundle's Resources/bin; Windows: in the Rhino System dir next to
        /// RhinoCommon.dll. Returns null when not found.
        /// </summary>
        static string? ResolveYak()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var sysDir = Path.GetDirectoryName(typeof(RhinoApp).Assembly.Location);
                    if (sysDir == null) return null;
                    var winYak = Path.Combine(sysDir, "yak.exe");
                    return File.Exists(winYak) ? winYak : null;
                }

                // macOS: <Rhino N.app>/Contents/MacOS/Rhinoceros → up to Contents.
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                var macosDir = exe != null ? Path.GetDirectoryName(exe) : null;
                var contents = macosDir != null ? Directory.GetParent(macosDir) : null;
                if (contents == null) return null;
                var macYak = Path.Combine(contents.FullName, "Resources", "bin", "yak");
                return File.Exists(macYak) ? macYak : null;
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] ResolveYak threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        static void ShowInfo(string text) =>
            MessageBox.Show(text, DialogCaption, MessageBoxButtons.OK, MessageBoxType.Information);

        static void ShowError(string text) =>
            MessageBox.Show(text, DialogCaption, MessageBoxButtons.OK, MessageBoxType.Error);
    }
}
