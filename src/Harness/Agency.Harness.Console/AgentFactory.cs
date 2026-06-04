
using Agency.Harness;
using Agency.Harness.Hooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Console;
internal sealed class AgentFactory : IAgentFactory
{
    private readonly Models models;
    private readonly ILogger<Agent> logger;
    private readonly AgentOptions options;

    public AgentFactory(
        Models models,
        ILogger<Agent> logger,
        IOptions<AgentOptions> optionsAccessor)
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

        // Compose baseline-first per spec §6.5.
        // BaselineHooks is null when memory is disabled; UserHooks is null unless
        // the host configures it via AgentOptions. All combinations handled:
        //   baseline only  → use baseline
        //   user only      → use user
        //   both           → baseline.Compose(user) so baseline always runs first
        //   neither        → null (no hooks)
        AgentHooks? hooks =
            (this.options.BaselineHooks, this.options.UserHooks) switch
            {
                (AgentHooks baseline, AgentHooks user) => baseline.Compose(user),
                (AgentHooks baseline, null) => baseline,
                (null, AgentHooks user) => user,
                _ => null,
            };

        var (chatClient, clientType) = this.models.CreateChatClient(clientName);
        return new Agent(chatClient, modelName, clientType, null, hooks, logger: this.logger);
    }
}
