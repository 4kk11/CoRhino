using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoRhino.Panel
{
    /// <summary>
    /// User-facing panel settings, persisted at ~/.corhino/settings.json (the
    /// same user-owned-state stance as session-names.json). Loaded from disk at
    /// every read site instead of cached — spawns happen on background threads
    /// and the settings drawer writes on the UI thread; re-reading a tiny file
    /// is cheaper than getting cross-thread cache invalidation right, and it
    /// picks up hand-edits for free.
    /// </summary>
    internal sealed class PanelSettings
    {
        /// <summary>
        /// Run claude's Bash tool inside the OS-level sandbox (macOS Seatbelt),
        /// confining writes to ~/.corhino. Spawn-time only: flipping it affects
        /// tabs created afterwards, never running sessions. Off by default —
        /// the sandbox changes the approval flow (in-sandbox commands can
        /// auto-run), which the user should opt into knowingly.
        /// </summary>
        [JsonPropertyName("sandboxEnabled")]
        public bool SandboxEnabled { get; set; }

        /// <summary>
        /// Whether claude's sandbox can work on this OS at all. Seatbelt is
        /// macOS-only and the Linux path (bubblewrap) is irrelevant — Rhino
        /// runs on macOS and Windows, so this is effectively "not Windows".
        /// The UI shows the toggle disabled (with a note) when false.
        /// </summary>
        public static bool SandboxSupported =>
            !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static PanelSettings Load()
        {
            try
            {
                if (File.Exists(Workspace.SettingsPath))
                    return JsonSerializer.Deserialize<PanelSettings>(
                        File.ReadAllText(Workspace.SettingsPath)) ?? new PanelSettings();
            }
            catch (Exception ex)
            {
                // A corrupt settings file must not take the panel down; fall
                // back to defaults and let the next Save overwrite it.
                Log.Write($"[CoRhino] settings.json unreadable, using defaults: {ex.Message}");
            }
            return new PanelSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Workspace.SettingsPath)!);
            File.WriteAllText(
                Workspace.SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
