namespace Agency.Agentic.Console;

using Agency.Agentic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    public Agent CreateAgent(string? clientName, string? modelName, bool stream)
    {
        clientName = !string.IsNullOrEmpty(clientName)
            ? clientName
            : this.options.DefaultClientName ?? throw new InvalidOperationException("DefaultClientName must be specified in the configuration.");

        modelName = !string.IsNullOrEmpty(modelName)
            ? modelName
            : this.options.DefaultModel ?? throw new InvalidOperationException("DefaultModel must be specified in the configuration.");

        var llmClient = this.models.CreateLlmClient(clientName);
        return new Agent(llmClient, modelName, null, stream, this.logger);
    }
}
