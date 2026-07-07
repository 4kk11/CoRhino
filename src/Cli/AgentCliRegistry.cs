namespace CoRhino.Cli
{
    /// <summary>
    /// Kind → adapter instance. Adapters are stateless singletons; per-spawn
    /// state travels in SpawnContext. Only Claude is registered so far.
    /// </summary>
    internal static class AgentCliRegistry
    {
        public static readonly IAgentCli Claude = new ClaudeCli();
    }
}
