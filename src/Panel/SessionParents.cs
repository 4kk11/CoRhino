using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// Session lineage from orchestrated spawns, persisted as a flat
    /// {childSessionId: parentSessionId} map in ~/.corhino/session-parents.json.
    /// Written once at spawnTab time (the only moment the parent/child pair is
    /// known — AgentTab.ParentAgentId is runtime-only and the tabs manifest
    /// dies with the panel) so the Sessions Drawer can mark agent-spawned
    /// sessions after every restart. Same discipline as SessionNames: one lock,
    /// write-through, temp+move, corrupt/missing file degrades to empty. Never
    /// pruned in V1 (entries are a few bytes; orphans from deleted sessions are
    /// harmless — see roadmap's session-names pruning item, shared fate).
    /// </summary>
    static class SessionParents
    {
        static readonly object Gate = new();

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static Dictionary<string, string> LoadAll()
        {
            lock (Gate)
            {
                try
                {
                    var path = Workspace.SessionParentsPath;
                    if (!File.Exists(path)) return new();
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] session-parents.json unreadable (treating as empty): {ex.GetType().Name}: {ex.Message}");
                    return new();
                }
            }
        }

        public static void Set(string childSessionId, string parentSessionId)
        {
            lock (Gate)
            {
                var all = LoadAll(); // reentrant lock — same thread, safe
                all[childSessionId] = parentSessionId;
                try
                {
                    var path = Workspace.SessionParentsPath;
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] session-parents.json write failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
