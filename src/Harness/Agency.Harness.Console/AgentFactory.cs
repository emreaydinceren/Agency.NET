
using Agency.Harness;
using Agency.Harness.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Console;
internal sealed class AgentFactory : IAgentFactory
{
    private readonly Models models;
    private readonly ILogger<Agent> logger;
    private readonly AgentOptions options;
    private readonly AgentHooks? _hooks;

    public AgentFactory(
        Models models,
        ILogger<Agent> logger,
        IOptions<AgentOptions> optionsAccessor,
        IServiceProvider serviceProvider)
    {
        this.models = models;
        this.logger = logger;
        this.options = optionsAccessor.Value;
        // Resolve AgentHooks optionally — null when memory is disabled and the singleton
        // was never registered. This keeps the factory working in both modes.
        this._hooks = serviceProvider.GetService<AgentHooks>();
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
        return new Agent(chatClient, modelName, clientType, null, this._hooks, logger: this.logger);
    }
}
