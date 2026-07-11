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

    /// <param name="models">Provides chat clients for the configured LLM providers.</param>
    /// <param name="logger">Logger passed through to created <see cref="Agent"/> instances.</param>
    /// <param name="optionsAccessor">Supplies the <see cref="AgentOptions"/> used to resolve defaults and fold hooks.</param>
    /// <param name="permissions">Optional permission evaluator passed through to created agents.</param>
    /// <param name="timeProvider">Optional clock passed through to created agents; defaults to <see cref="TimeProvider.System"/>.</param>
    public AgentFactory(
        Models models,
        ILogger<Agent> logger,
        IOptions<AgentOptions> optionsAccessor,
        IPermissionEvaluator? permissions = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);

        this.models = models;
        this.logger = logger;
        this.options = optionsAccessor.Value;
        this.permissions = permissions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates an <see cref="Agent"/> configured for the named client/model, falling back to
    /// <see cref="AgentOptions.DefaultClientName"/>/<see cref="AgentOptions.DefaultModel"/> when either is
    /// null or empty, and folding baseline/configured/user hooks via <see cref="AgentHooksExtensions.Fold"/>.
    /// </summary>
    /// <param name="clientName">The LLM client configuration name; falls back to <see cref="AgentOptions.DefaultClientName"/> when null or empty.</param>
    /// <param name="modelName">The model identifier; falls back to <see cref="AgentOptions.DefaultModel"/> when null or empty.</param>
    /// <returns>A new <see cref="Agent"/> wired with the resolved chat client, hooks, and permission evaluator.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="clientName"/> is not specified and <see cref="AgentOptions.DefaultClientName"/> is not
    /// configured, or <paramref name="modelName"/> is not specified and <see cref="AgentOptions.DefaultModel"/>
    /// is not configured.
    /// </exception>
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
