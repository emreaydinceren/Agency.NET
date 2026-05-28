using Agency.Memory.Common.Events;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Consolidator.DependencyInjection;

/// <summary>
/// Extension methods for registering the consolidator background service with
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// Call <c>AddAgencyConsolidator()</c> after <c>AddAgencyMemory()</c> and after an
/// <see cref="IMemoryStore"/> implementation and an <see cref="IChatClient"/> have been
/// registered. The consolidator subscribes to <see cref="DistillationCompletedEvent"/>
/// via <see cref="IAsyncEventBus"/> to enqueue <see cref="Common.Jobs.ConsolidationJob"/>s.
/// </remarks>
public static class ConsolidatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConsolidatorBackgroundService"/> and the sub-agent runner factory.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional action to configure <see cref="ConsolidatorOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyConsolidator(
        this IServiceCollection services,
        Action<ConsolidatorOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<ConsolidatorOptions>();
        }

        services.AddHostedService<ConsolidatorBackgroundService>(sp =>
        {
            IChatClient llm = sp.GetRequiredService<IChatClient>();
            IMemoryStore store = sp.GetRequiredService<IMemoryStore>();
            IAsyncEventBus eventBus = sp.GetRequiredService<IAsyncEventBus>();
            IOptions<ConsolidatorOptions> opts = sp.GetRequiredService<IOptions<ConsolidatorOptions>>();
            ILogger<ConsolidatorBackgroundService> logger =
                sp.GetRequiredService<ILogger<ConsolidatorBackgroundService>>();
            ILogger<Agency.Agentic.Agent> agentLogger =
                sp.GetRequiredService<ILogger<Agency.Agentic.Agent>>();

            string model = opts.Value.Model ?? "default";

            Func<string, IReadOnlyList<Record>, CancellationToken, Task> runner =
                ConsolidatorSubAgentFactory.CreateRunner(llm, model, store, opts, agentLogger);

            return new ConsolidatorBackgroundService(store, runner, eventBus, opts, logger);
        });

        return services;
    }
}
