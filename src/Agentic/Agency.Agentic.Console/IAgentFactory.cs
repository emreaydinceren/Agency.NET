
using Agency.Agentic;

namespace Agency.Agentic.Console;
internal interface IAgentFactory
{
    Agent CreateAgent(string? clientName, string? modelName);
}
