using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Hooks;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Retrieval;
using Agency.Memory.Sql.Postgres;
using Agency.Embeddings.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.DependencyInjection;

/// <summary>
/// Extension methods for registering the Agency memory pipeline services with
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// Registers: options, the in-process event bus, the per-session channel registry,
/// the inactivity timer service, and the distiller background service.
/// Also builds and registers the baseline <see cref="AgentHooks"/> that wire
/// retrieval (<c>OnPreIteration</c>) and timer-restart (<c>OnAssistantTurn</c>).
///
/// <b>Reference direction (IQ-1):</b> This extension lives in
/// <c>Agency.Memory.Distiller</c> (which references both
/// <c>Agency.Memory.Common</c> and <c>Agency.Memory.Retrieval</c>) so it can
/// directly call <c>RetrievalEngine.RetrieveAsync</c> and
/// <c>RetrievalGate.ShouldRetrieveAsync</c> without creating a circular project
/// reference. The callback delegates are passed into
/// <see cref="MemoryHookFactory.Build"/> in <c>Agency.Memory.Common</c>.
/// </remarks>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Agency memory pipeline services and configures baseline hooks.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configureMemory">Optional action to configure <see cref="MemoryOptions"/>.</param>
    /// <param name="configureDistiller">Optional action to configure <see cref="DistillerOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgencyMemory(
        this IServiceCollection services,
        Action<MemoryOptions>? configureMemory = null,
        Action<DistillerOptions>? configureDistiller = null)
    {
        // Options registration.
        if (configureMemory is not null)
        {
            services.Configure(configureMemory);
        }
        else
        {
            services.AddOptions<MemoryOptions>();
        }

        if (configureDistiller is not null)
        {
            services.Configure(configureDistiller);
        }
        else
        {
            services.AddOptions<DistillerOptions>();
        }

        // In-process event bus.
        services.AddSingleton<IAsyncEventBus>(sp =>
            new InMemoryEventBus(sp.GetRequiredService<ILogger<InMemoryEventBus>>()));

        // Per-session channel registry (singleton; holds all per-session channels).
        services.AddSingleton<ChannelSessionRegistry>();

        // Conversation manager registry.
        services.AddSingleton<IConversationManagerRegistry, InMemoryConversationManagerRegistry>();

        // Inactivity timer service.
        services.AddSingleton<InactivityTimerService>(sp => new InactivityTimerService(
            sp.GetRequiredService<ChannelSessionRegistry>(),
            sp.GetRequiredService<IOptions<DistillerOptions>>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<InactivityTimerService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<InactivityTimerService>());

        // Distiller background service.
        services.AddHostedService<DistillerBackgroundService>(sp => new DistillerBackgroundService(
            sp.GetRequiredService<ChannelSessionRegistry>(),
            sp.GetRequiredService<IConversationManagerRegistry>(),
            sp.GetRequiredService<ILlmClientAdapter>(),
            sp.GetRequiredService<IEmbeddingGenerator>(),
            sp.GetRequiredService<IMemoryStore>(),
            new WatermarkStoreAdapter(sp.GetRequiredService<WatermarkRepository>()),
            new DeadLetterStoreAdapter(sp.GetRequiredService<DeadLetterRepository>()),
            sp.GetRequiredService<IAsyncEventBus>(),
            sp.GetRequiredService<IOptions<DistillerOptions>>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<DistillerBackgroundService>>()));

        // Register the baseline hooks factory result as a singleton.
        // The factory builds the hook delegates by resolving IMemoryStore, IEmbeddingGenerator,
        // IOptions<MemoryOptions>, and InactivityTimerService from the DI container.
        services.AddSingleton<AgentHooks>(sp =>
        {
            IMemoryStore store = sp.GetRequiredService<IMemoryStore>();
            IEmbeddingGenerator embedder = sp.GetRequiredService<IEmbeddingGenerator>();
            IOptions<MemoryOptions> memoryOptions = sp.GetRequiredService<IOptions<MemoryOptions>>();
            InactivityTimerService timerService = sp.GetRequiredService<InactivityTimerService>();

            // Build retrieval engine with the resolved dependencies.
            var engine = new RetrievalEngine(store, embedder, memoryOptions);

            // Retrieval callback: gated vector search injecting into Context.
            Func<Context, CancellationToken, Task> retrievalCallback = async (ctx, ct) =>
            {
                bool shouldRetrieve = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct)
                    .ConfigureAwait(false);
                if (shouldRetrieve)
                {
                    await engine.RetrieveAsync(ctx, ct).ConfigureAwait(false);
                }
            };

            // Timer-restart callback: ONLY restart the timer, no other side effects (Spec §14.9).
            Func<AssistantTurnHookContext, CancellationToken, Task> timerCallback = (hookCtx, _) =>
            {
                string userId = hookCtx.AgentContext.User.Id ?? string.Empty;
                // SessionId is not in Context; use a stable derived key.
                // Agents must register their session in the registry before using these hooks.
                string sessionId = hookCtx.AgentContext.User.Id ?? string.Empty;
                int turnIndex = hookCtx.AgentContext.Conversation.Messages.Count;
                timerService.Restart(userId, sessionId, turnIndex);
                return Task.CompletedTask;
            };

            return MemoryHookFactory.Build(retrievalCallback, timerCallback);
        });

        return services;
    }
}
