using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// User-given session names, persisted as a flat {sessionId: name} map in
    /// ~/.corhino/session-names.json. Keyed by sessionId (not tab) so a
    /// name survives /exit + resume and shows up in both the tab label and the
    /// Sessions Drawer. Renames arrive on the UI thread while sessions scans read
    /// on a background thread → every access goes through one lock with
    /// write-through persistence (no cache to go stale). A corrupt or missing file
    /// degrades to an empty map. Deliberately never pruned in V1: a live tab's id
    /// may have no transcript on disk yet, so pruning against the scan would eat
    /// fresh names (orphans from renamed-then-deleted sessions are harmless).
    /// </summary>
    static class SessionNames
    {
        static readonly object Gate = new();

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static Dictionary<string, string> LoadAll()
        {
            lock (Gate)
            {
                try
                {
                    var path = Workspace.SessionNamesPath;
                    if (!File.Exists(path)) return new();
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] session-names.json unreadable (treating as empty): {ex.GetType().Name}: {ex.Message}");
                    return new();
                }
            }
        }

        /// <summary>Set a session's name; null/whitespace removes it (revert to auto title).</summary>
        public static void Set(string sessionId, string? name)
        {
            lock (Gate)
            {
                var all = LoadAll(); // reentrant lock — same thread, safe
                if (string.IsNullOrWhiteSpace(name)) all.Remove(sessionId);
                else all[sessionId] = name!.Trim();
                try
                {
                    // Temp + move: a crash mid-write must not corrupt the file
                    // (LoadAll degrades a corrupt file to empty = every name lost).
                    var path = Workspace.SessionNamesPath;
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] session-names.json write failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
