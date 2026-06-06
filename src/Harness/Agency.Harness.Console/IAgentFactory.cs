namespace Agency.Harness.Console;
internal interface IAgentFactory
{
    Agent CreateAgent(string? clientName, string? modelName);
}
