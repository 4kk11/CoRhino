using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// One skill as shown in the panel's Skills Drawer. Serialized camelCase
    /// straight into the WebView (see SkillsCatalog.ToJson).
    /// </summary>
    sealed class SkillInfo
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        /// <summary>"user" (~/.corhino/skills) or "bundled" (cc-plugin next to the assembly).</summary>
        public required string Source { get; init; }
        /// <summary>Absolute path of the SKILL.md — what openSkill launches.</summary>
        public required string Path { get; init; }
        public required bool NeedsReload { get; init; }
    }

    /// <summary>
    /// Scans the two skill plugins a session loads (the user's ~/.corhino/skills and
    /// the bundled cc-plugin) into SkillInfo DTOs for the Skills Drawer. Knows nothing
    /// about the WebView or tabs: needsReload is computed against the caller-supplied
    /// per-tab load timestamp, because claude's actual plugin state can't be read from
    /// outside — mtime vs. "when this tab last (re)loaded" is a safe approximation
    /// (a false ⚠ still converges to the truth once the user hits ⟳).
    /// </summary>
    static class SkillsCatalog
    {
        // Paths returned by the most recent Scan. openSkill validates against this
        // set so the WebView can only launch files the drawer actually listed, never
        // an arbitrary path. Replaced wholesale (never mutated) → volatile read is enough.
        static volatile HashSet<string>? _knownPaths;

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        static string BundledPluginRoot => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(SkillsCatalog).Assembly.Location) ?? string.Empty,
            "cc-plugin");

        public static IReadOnlyList<SkillInfo> Scan(DateTime skillsLoadedAtUtc)
        {
            var skills = new List<SkillInfo>();
            ScanPlugin(Workspace.UserSkillsRoot, "user", skillsLoadedAtUtc, skills);
            ScanPlugin(BundledPluginRoot, "bundled", skillsLoadedAtUtc, skills);
            _knownPaths = skills.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
            return skills;
        }

        public static bool IsKnownSkillPath(string path) =>
            _knownPaths?.Contains(path) == true;

        public static string ToJson(string agentId, IReadOnlyList<SkillInfo> skills) =>
            JsonSerializer.Serialize(new { agentId, skills }, JsonOptions);

        static void ScanPlugin(string pluginRoot, string source, DateTime loadedAtUtc, List<SkillInfo> into)
        {
            var skillsDir = System.IO.Path.Combine(pluginRoot, "skills");

            // Deleting/renaming the skills tree is exactly what fires the watcher
            // that triggers this scan, so the enumeration can race the deletion —
            // and the watcher push path has no message-pump catch above it (an
            // unhandled throw would land on Rhino's UI thread).
            string[] dirs;
            try
            {
                if (!Directory.Exists(skillsDir)) return;
                dirs = Directory.GetDirectories(skillsDir);
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] Skill scan failed ({skillsDir}): {ex.GetType().Name}: {ex.Message}");
                return;
            }

            foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
            {
                var skillMd = System.IO.Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillMd)) continue;

                // A broken/unreadable SKILL.md still gets a row (name = dir name) —
                // silently dropping it from the list would be more confusing than a
                // blank description.
                string? name = null, description = null;
                try
                {
                    (name, description) = ParseFrontmatter(File.ReadAllText(skillMd));
                }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] Skill read failed ({skillMd}): {ex.GetType().Name}: {ex.Message}");
                }

                var needsReload = false;
                try { needsReload = File.GetLastWriteTimeUtc(skillMd) > loadedAtUtc; }
                catch (Exception ex)
                {
                    Log.Write($"[CoRhino] Skill mtime read failed ({skillMd}): {ex.Message}");
                }

                into.Add(new SkillInfo
                {
                    Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(dir) : name!,
                    Description = description ?? string.Empty,
                    Source = source,
                    Path = skillMd,
                    NeedsReload = needsReload,
                });
            }
        }

        /// <summary>
        /// Minimal frontmatter reader: if the document starts with a `---` line, pick
        /// the single-line `name:` / `description:` values up to the closing `---`.
        /// Deliberately not a YAML parser (multi-line values just come back null and
        /// the UI shows a fallback — nothing breaks). Pure function, kept free of I/O
        /// so it can be unit-tested when the repo grows a test harness.
        /// </summary>
        internal static (string? Name, string? Description) ParseFrontmatter(string text)
        {
            string? name = null, description = null;
            // A BOM would make the first line read "\uFEFF---" and miss the fence.
            var lines = text.TrimStart('\uFEFF').Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd('\r').Trim() != "---")
                return (name, description);

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Trim() == "---") // closing fence; ignore the body
                    return (name, description);
                if (name == null && TryGetValue(line, "name", out var n)) name = n;
                else if (description == null && TryGetValue(line, "description", out var d)) description = d;
            }
            // No closing fence → not frontmatter after all; a column-0 "name:" in
            // the body must not masquerade as metadata.
            return (null, null);
        }

        static bool TryGetValue(string line, string key, out string? value)
        {
            value = null;
            var prefix = key + ":";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var v = line.Substring(prefix.Length).Trim();
            // YAML block/folded scalar (`description: >` / `|` / `>-` …): the value
            // lives on the following indented lines, which this parser deliberately
            // doesn't read — fall back rather than show a literal ">" in the drawer.
            if (v is ">" or "|" or ">-" or ">+" or "|-" or "|+") return false;
            // Strip one layer of matching quotes ('...' / "..."), the common YAML style.
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
                v = v.Substring(1, v.Length - 2);
            if (v.Length == 0) return false;
            value = v;
            return true;
        }
    }
}
