using Agency.Harness;
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
/// Also builds the baseline <see cref="AgentHooks"/> and sets them on
/// <see cref="AgentOptions.BaselineHooks"/> via <c>IPostConfigureOptions</c> per spec §6.5.
/// <see cref="AgentFactory"/> composes <c>BaselineHooks</c> with <c>UserHooks</c> so the
/// baseline always runs first (retrieval, timer restart) before any user-supplied hooks.
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

        // Build baseline hooks and set them on AgentOptions.BaselineHooks (spec §6.5).
        // PostConfigure runs after all Configure<AgentOptions> calls so it does not
        // overwrite host-supplied UserHooks; composition happens in AgentFactory.
        services.AddSingleton<IPostConfigureOptions<AgentOptions>>(sp =>
            new PostConfigureOptions<AgentOptions>(
                name: null,
                action: agentOpts =>
                {
                    IMemoryStore store = sp.GetRequiredService<IMemoryStore>();
                    IEmbeddingGenerator embedder = sp.GetRequiredService<IEmbeddingGenerator>();
                    IOptions<MemoryOptions> memoryOptions = sp.GetRequiredService<IOptions<MemoryOptions>>();
                    InactivityTimerService timerService = sp.GetRequiredService<InactivityTimerService>();
                    IConversationManagerRegistry conversationRegistry =
                        sp.GetRequiredService<IConversationManagerRegistry>();
                    ChannelSessionRegistry channelRegistry = sp.GetRequiredService<ChannelSessionRegistry>();

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
                        string sessionId = hookCtx.AgentContext.Session.Id ?? string.Empty;
                        int turnIndex = hookCtx.AgentContext.Conversation.Messages.Count;
                        timerService.Restart(userId, sessionId, turnIndex, hookCtx.AgentContext.Focus);
                        return Task.CompletedTask;
                    };

                    // Session-started callback: register the live conversation manager so the distiller
                    // can read session turns by session id, AND register per-session memory tools.
                    Func<SessionStartedHookContext, CancellationToken, Task> convoCallback =
                        ConversationRegistrationHook.Create(conversationRegistry);

                    Func<SessionStartedHookContext, CancellationToken, Task> sessionStartedCallback =
                        async (hookCtx, ct) =>
                        {
                            await convoCallback(hookCtx, ct).ConfigureAwait(false);
                            await MemorySessionTools.RegisterInto(hookCtx.AgentContext, channelRegistry, store, ct).ConfigureAwait(false);
                        };

                    // Session-end callback: enqueue a terminal SessionDisposed distillation job.
                    Func<SessionEndedHookContext, CancellationToken, Task> sessionEndCallback =
                        SessionEndHook.Create(channelRegistry);

                    agentOpts.BaselineHooks = MemoryHookFactory.Build(
                        retrievalCallback, timerCallback, sessionStartedCallback, sessionEndCallback);
                }));

        return services;
    }
}
