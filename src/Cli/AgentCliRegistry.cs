using System;

namespace CoRhino.Cli
{
    /// <summary>
    /// Kind → adapter instance. Adapters are stateless singletons; per-spawn
    /// state travels in SpawnContext.
    /// </summary>
    internal static class AgentCliRegistry
    {
        public static readonly IAgentCli Claude = new ClaudeCli();
        public static readonly IAgentCli Codex = new CodexCli();

        public static IAgentCli For(AgentCliKind kind) => kind switch
        {
            AgentCliKind.Claude => Claude,
            AgentCliKind.Codex => Codex,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No adapter registered."),
        };
    }
}
