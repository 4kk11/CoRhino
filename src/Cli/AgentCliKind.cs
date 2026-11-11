namespace CoRhino.Cli
{
    /// <summary>The agent CLIs a tab can run. Each maps to one IAgentCli adapter.</summary>
    internal enum AgentCliKind
    {
        Claude,
        Codex,
        Pi,
    }
}
