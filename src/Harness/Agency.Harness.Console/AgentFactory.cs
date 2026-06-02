
using Agency.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Console;
internal sealed class AgentFactory : IAgentFactory
{
    private readonly Models models;
    private readonly ILogger<Agent> logger;
    private readonly AgentOptions options;

    public AgentFactory(Models models, ILogger<Agent> logger, IOptions<AgentOptions> optionsAccessor)
    {
        this.models = models;
        this.logger = logger;
        this.options = optionsAccessor.Value;
    }

    public Agent CreateAgent(string? clientName, string? modelName)
    {
        clientName = !string.IsNullOrEmpty(clientName)
            ? clientName
            : this.options.DefaultClientName ?? throw new InvalidOperationException("DefaultClientName must be specified in the configuration.");

        modelName = !string.IsNullOrEmpty(modelName)
            ? modelName
            : this.options.DefaultModel ?? throw new InvalidOperationException("DefaultModel must be specified in the configuration.");

        var (chatClient, clientType) = this.models.CreateChatClient(clientName);
        return new Agent(chatClient, modelName, clientType, null, logger: this.logger);
    }
}
