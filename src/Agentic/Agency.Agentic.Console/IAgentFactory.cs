namespace Agency.Agentic.Console;

using Agency.Agentic;

internal interface IAgentFactory
{
    Agent CreateAgent(string? clientName, string? modelName, bool stream);
}
