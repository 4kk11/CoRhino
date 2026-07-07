using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// Codex sessionId → layer token, persisted as a flat map in
    /// ~/.corhino/session-layers.json. Claude never needs this (its layer is
    /// derived from the sessionId), but codex mints the token BEFORE the id is
    /// known (the id is discovered after spawn), so the pairing must be stored
    /// for resume to restore the same layer. Same regime as SessionNames: one
    /// lock, write-through, temp+move persistence, corrupt file degrades to
    /// empty, never pruned in V1 (an orphan token is harmless).
    /// </summary>
    static class SessionLayers
    {
        static readonly object Gate = new();

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static bool TryGet(string sessionId, out string layerToken)
        {
            lock (Gate)
            {
                var hit = LoadAll().TryGetValue(sessionId, out var v);
                layerToken = v ?? string.Empty;
                return hit;
            }
        }

        public static void Set(string sessionId, string layerToken)
        {
            lock (Gate)
            {
                var all = LoadAll(); // reentrant lock — same thread, safe
                all[sessionId] = layerToken;
                try
                {
                    var path = Workspace.SessionLayersPath;
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] session-layers.json write failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        static Dictionary<string, string> LoadAll()
        {
            lock (Gate)
            {
                try
                {
                    var path = Workspace.SessionLayersPath;
                    if (!File.Exists(path)) return new();
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] session-layers.json unreadable (treating as empty): {ex.GetType().Name}: {ex.Message}");
                    return new();
                }
            }
        }
    }
}
