using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CoRhino.Panel
{
    /// <summary>
    /// One parsed orchestration command (see plans/agent-orchestration-design.md §3).
    /// Field presence is action-dependent and enforced by <see cref="AgentCommands.TryParse"/>.
    /// </summary>
    sealed class AgentCommand
    {
        public string Id = "";
        public string From = "";
        public string Action = "";
        public string? Prompt;          // spawnTab
        public string? PermissionMode;  // spawnTab (optional)
        public string? Name;            // spawnTab (optional)
        public string? Target;          // sendPrompt / closeTab
        public string? Text;            // sendPrompt
    }

    /// <summary>
    /// The UI-independent half of the orchestration command protocol: parsing +
    /// schema validation of request files, response writing, and stale-file
    /// sweeping. Same discipline as SkillsCatalog: pure facts in and out, no
    /// panel state, callable from any thread. Which tab may do what
    /// (parent/child scope, tab limits) is the PANEL's job — this class only
    /// answers "is this a well-formed request" and "write this result down".
    /// </summary>
    static class AgentCommands
    {
        /// <summary>Error vocabulary of the protocol (design §3.3). Kept as constants so responses never drift.</summary>
        public const string ErrInvalidRequest = "invalid-request";
        public const string ErrUnsupportedCli = "unsupported-cli";
        public const string ErrNotYourChild = "not-your-child";
        public const string ErrTargetNotRunning = "target-not-running";
        public const string ErrTabLimitReached = "tab-limit-reached";

        public static readonly string[] KnownActions = { "spawnTab", "sendPrompt", "closeTab" };

        /// <summary>
        /// True when this file name is a command addressed to <paramref name="port"/>'s
        /// panel: cmd-&lt;port&gt;-&lt;id&gt;.json. The port prefix is the same panel-routing
        /// convention the status files use, just in the opposite direction.
        /// </summary>
        public static bool IsCommandForPort(string fileName, int port) =>
            fileName.StartsWith($"cmd-{port}-", StringComparison.Ordinal) &&
            fileName.EndsWith(".json", StringComparison.Ordinal);

        /// <summary>
        /// Parse and schema-check one request payload. Returns false with an
        /// error word on any violation; <paramref name="id"/> is surfaced even
        /// for invalid requests when extractable, so the panel can still answer
        /// with an error response instead of going silent.
        /// </summary>
        public static bool TryParse(string json, out AgentCommand cmd, out string error, out string? id)
        {
            cmd = new AgentCommand();
            error = ErrInvalidRequest;
            id = null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return false; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                id = StringField(root, "id");
                // The id becomes the response FILE NAME: a real uuid only —
                // stricter than "no path separators", and it lets the panel
                // enforce filename-id == body-id (a body claiming another id
                // must not be able to overwrite that id's response).
                if (id == null || !Guid.TryParse(id, out _)) { id = null; return false; }
                var from = StringField(root, "from");
                var action = StringField(root, "action");
                if (from == null || action == null || Array.IndexOf(KnownActions, action) < 0) return false;

                // V1 speaks claude only. An explicit non-claude cli is the one
                // schema violation with its own error word, so orchestrators
                // learn "not yet" rather than "you typed it wrong".
                var cli = StringField(root, "cli");
                if (cli != null && cli != "claude") { error = ErrUnsupportedCli; return false; }

                cmd.Id = id;
                cmd.From = from;
                cmd.Action = action;
                cmd.Prompt = StringField(root, "prompt");
                cmd.PermissionMode = StringField(root, "permissionMode");
                cmd.Name = StringField(root, "name");
                cmd.Target = StringField(root, "target");
                cmd.Text = StringField(root, "text");

                switch (action)
                {
                    case "spawnTab":
                        if (string.IsNullOrWhiteSpace(cmd.Prompt)) return false;
                        // Closed vocabulary: an arbitrary string would reach
                        // `claude --permission-mode <x>` and kill the child at
                        // boot — reject it here where the requester gets told.
                        if (cmd.PermissionMode != null &&
                            cmd.PermissionMode is not ("default" or "plan" or "acceptEdits" or "bypassPermissions"))
                            return false;
                        break;
                    case "sendPrompt":
                        if (string.IsNullOrEmpty(cmd.Target) || string.IsNullOrWhiteSpace(cmd.Text)) return false;
                        break;
                    case "closeTab":
                        if (string.IsNullOrEmpty(cmd.Target)) return false;
                        break;
                }
                return true;
            }
        }

        static string? StringField(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        /// <summary>
        /// Read a request file the watcher just claimed. One short retry covers
        /// a writer that skipped the temp+move contract; a still-unreadable
        /// file surfaces as the caller's catch.
        /// </summary>
        public static string ReadRequest(string path)
        {
            try { return File.ReadAllText(path); }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(30);
                return File.ReadAllText(path);
            }
        }

        /// <summary>Write responses/&lt;id&gt;.json (temp+move so the polling agent never sees a torn file).</summary>
        public static void WriteResponse(string id, object payload)
        {
            try
            {
                Directory.CreateDirectory(Workspace.ResponsesDir);
                var path = Path.Combine(Workspace.ResponsesDir, id + ".json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // A lost response degrades to the orchestrator's poll timeout —
                // never let it take the dispatcher down.
                Log.Write($"[CoRhino] command response write failed ({id}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void WriteError(string id, string action, string error) =>
            WriteResponse(id, new { ok = false, action, error });

        /// <summary>
        /// Panel-construction sweep: drop leftover requests addressed to this
        /// port (their panel is gone — nobody will ever execute them) and
        /// responses old enough that their requester has long stopped polling.
        /// Everything here is best-effort; the protocol self-heals around gaps.
        /// </summary>
        public static void SweepStale(int port)
        {
            try
            {
                Directory.CreateDirectory(Workspace.CommandsDir);
                Directory.CreateDirectory(Workspace.ResponsesDir);
                foreach (var file in Directory.GetFiles(Workspace.CommandsDir))
                {
                    if (IsCommandForPort(Path.GetFileName(file), port))
                        TryDelete(file);
                }
                var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
                foreach (var file in Directory.GetFiles(Workspace.ResponsesDir))
                {
                    try { if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file); }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[CoRhino] command sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Write($"[CoRhino] command file delete failed: {ex.Message}"); }
        }

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    }
}
