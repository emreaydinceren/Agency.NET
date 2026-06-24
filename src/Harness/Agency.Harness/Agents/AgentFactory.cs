using Agency.Harness.Hooks;
using Agency.Harness.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness;

/// <inheritdoc />
public sealed class AgentFactory : IAgentFactory
{
    private readonly Models models;
    private readonly ILogger<Agent> logger;
    private readonly AgentOptions options;
    private readonly IPermissionEvaluator? permissions;
    private readonly TimeProvider timeProvider;

    public AgentFactory(
        Models models,
        ILogger<Agent> logger,
        IOptions<AgentOptions> optionsAccessor,
        IPermissionEvaluator? permissions = null,
        TimeProvider? timeProvider = null)
    {
        this.models = models;
        this.logger = logger;
        this.options = optionsAccessor.Value;
        this.permissions = permissions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Agent CreateAgent(string? clientName, string? modelName)
    {
        clientName = !string.IsNullOrEmpty(clientName)
            ? clientName
            : this.options.DefaultClientName ?? throw new InvalidOperationException("DefaultClientName must be specified in the configuration.");

        modelName = !string.IsNullOrEmpty(modelName)
            ? modelName
            : this.options.DefaultModel ?? throw new InvalidOperationException("DefaultModel must be specified in the configuration.");

        AgentHooks? hooks = AgentHooksExtensions.Fold(
            this.options.BaselineHooks,
            this.options.ConfiguredHooks,
            this.options.UserHooks);

        var (chatClient, clientType) = this.models.CreateChatClient(clientName);
        return new Agent(chatClient, modelName, clientType, null, hooks, permissions: this.permissions, logger: this.logger, timeProvider: this.timeProvider, logToolPayloads: this.options.LogToolPayloads);
    }
}
