using Agency.Harness;
using Agency.Harness.Hooks;
using Agency.Embeddings.OpenAI;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.DependencyInjection;
using Agency.Memory.Distiller.DependencyInjection;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using System.Collections.Concurrent;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;

namespace Agency.Memory.Functional.Test.Infrastructure;

/// <summary>
/// xUnit <see cref="IAsyncLifetime"/> class-scoped fixture that boots a real
/// <see cref="IHost"/> wiring the distiller background service, the consolidator
/// background service, the inactivity-timer service, plus
/// <see cref="PostgresMemoryStore"/>, a real <see cref="ILlmClientAdapter"/>,
/// and a real <see cref="IEmbeddingGenerator"/> per Memory-Specifications.md §10.
/// </summary>
/// <remarks>
/// <para>
/// Typical usage:
/// <code>
/// public sealed class MyTests(MemoryE2EFixture fixture) : IClassFixture&lt;MemoryE2EFixture&gt;
/// {
///     [Fact, Trait("Category", "Functional")]
///     public async Task MyTest()
///     {
///         if (fixture.PostgresSkipReason is not null) { Assert.Skip(fixture.PostgresSkipReason); return; }
///         if (fixture.LmStudioSkipReason is not null) { Assert.Skip(fixture.LmStudioSkipReason); return; }
///         // ... use fixture.Store, fixture.NewSession(...), fixture.WaitForDistillationAsync(...)
///     }
/// }
/// </code>
/// </para>
/// <para>
/// <b>DB isolation:</b> Use a unique <c>userId</c> per test (e.g.
/// <c>$"{nameof(MyTest)}-{Guid.NewGuid():N}"</c>). The fixture resets the schema once in
/// <see cref="InitializeAsync"/>; per-test isolation is achieved by partition key (<c>userId</c>).
/// </para>
/// <para>
/// <b>Hygiene sweeper wiring gap (TI-1):</b> <c>HygieneSweeperBackgroundService</c> is
/// <c>internal sealed</c> with no public DI extension in <c>Agency.Memory.Hygiene</c>.
/// The fixture cannot register it without modifying production code. Until a public
/// <c>AddAgencyHygiene()</c> extension is created and <c>InternalsVisibleTo</c> is granted,
/// Group 4 (Hygiene) tests must use <see cref="IMemoryStore"/> methods directly via the
/// <see cref="TimeShim"/> primitive rather than driving the sweeper via the hosted service.
/// This gap is logged as issue <b>TI-1</b>.
/// </para>
/// </remarks>
internal sealed class MemoryE2EFixture : IAsyncLifetime
{
    // Config keys shared with TestInfrastructure.
    private const string LmStudioBaseUrlKey = "MemoryFunctional:LmStudio:BaseUrl";
    private const string LmStudioApiKeyKey = "MemoryFunctional:LmStudio:ApiKey";
    private const string LmStudioChatModelKey = "MemoryFunctional:LmStudio:ChatModel";
    private const string LmStudioEmbeddingModelKey = "MemoryFunctional:LmStudio:EmbeddingModel";

    /// <summary>
    /// Fixed wall-clock instant injected into the conversational agent so the system-prompt
    /// "Current date/time (UTC)" line is byte-identical across runs, which keeps the agent's
    /// chat requests replayable from the HTTP cache. Must stay a hard-coded literal — a
    /// per-run <see cref="DateTimeOffset.UtcNow"/> would differ between the cache-record run
    /// and a later cache-replay run and defeat the cache.
    /// </summary>
    private static readonly DateTimeOffset DeterministicClock =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The actual embedding dimension produced by the configured LM Studio embedding model,
    /// detected at init time by probing the real embedder. Exposed so that group-test
    /// classes that use this fixture can construct dimension-compatible stub embedders.
    /// Defaults to 1536 until <see cref="InitializeAsync"/> completes.
    /// </summary>
    internal int EmbeddingDim { get; private set; } = 1536;

    private IHost? _host;
    private NpgsqlDataSource? _dataSource;
    private readonly ConcurrentBag<AgentEvent> _capturedEvents = [];
    private readonly List<IDisposable> _eventSubscriptions = [];

    // ── Skip-reason properties ────────────────────────────────────────────────

    /// <summary>
    /// Gets a non-null skip reason when Postgres is unreachable, or
    /// <see langword="null"/> when Postgres is reachable.
    /// Available after <see cref="InitializeAsync"/>.
    /// </summary>
    internal string? PostgresSkipReason { get; private set; }

    /// <summary>
    /// Gets a non-null skip reason when LM Studio is unreachable, or
    /// <see langword="null"/> when LM Studio is reachable.
    /// Available after <see cref="InitializeAsync"/>.
    /// </summary>
    internal string? LmStudioSkipReason { get; private set; }

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the DI service provider from the started host.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the fixture has not yet been initialised (i.e.
    /// <see cref="InitializeAsync"/> was not awaited or backends were unreachable).
    /// </exception>
    internal IServiceProvider Services =>
        this._host?.Services
        ?? throw new InvalidOperationException(
            "Fixture not initialised. Check PostgresSkipReason / LmStudioSkipReason first.");

    /// <summary>
    /// Gets the <see cref="IMemoryStore"/> singleton registered in the host.
    /// </summary>
    internal IMemoryStore Store =>
        this.Services.GetRequiredService<IMemoryStore>();

    /// <summary>
    /// Gets the in-process <see cref="IAsyncEventBus"/> singleton from the host.
    /// </summary>
    internal IAsyncEventBus EventBus =>
        this.Services.GetRequiredService<IAsyncEventBus>();

    /// <summary>
    /// Gets the <see cref="AgentHooks"/> singleton from the host.
    /// These are the baseline memory hooks built by <c>AddAgencyMemory</c> and include
    /// the gated retrieval callback wired to <c>OnPreIteration</c>.
    /// </summary>
    internal AgentHooks BaselineHooks =>
        this.Services.GetRequiredService<AgentHooks>();

    /// <summary>
    /// Gets a snapshot of all <see cref="AgentEvent"/>s captured since the host was started.
    /// The fixture automatically subscribes to <see cref="DistillationCompletedEvent"/>,
    /// <see cref="DistillationFailedEvent"/>, <see cref="ConsolidationCompletedEvent"/>,
    /// and <see cref="MemoryMutatedEvent"/>.
    /// </summary>
    internal IReadOnlyList<AgentEvent> CapturedEvents =>
        this._capturedEvents.ToArray();

    /// <summary>
    /// Gets a factory that creates a new <see cref="ChatSession"/> wired with the host's
    /// baseline memory hooks. Each invocation returns a fresh session; the conversation
    /// is registered with the <see cref="InMemoryConversationManagerRegistry"/> automatically.
    /// </summary>
    /// <remarks>
    /// Signature: <c>(string userId, string sessionId) => IChatSession</c>.
    /// The returned <see cref="IChatSession"/> exposes <see cref="IChatSession.SendAsync"/>
    /// and the underlying <see cref="IChatSession.Conversation"/> manager.
    /// </remarks>
    internal Func<string, string, IChatSession> NewSession
    {
        get
        {
            IServiceProvider services = this.Services;
            return (userId, sessionId) =>
            {
                IConfiguration config = services.GetRequiredService<IConfiguration>();
                IChatClient llmClient = services.GetRequiredService<IChatClient>();
                AgentHooks baselineHooks = services.GetRequiredService<AgentHooks>();
                string chatModel = config[LmStudioChatModelKey] ?? "local-model";

                var agent = new Agent(
                    llmClient,
                    chatModel,
                    clientType: "lmstudio",
                    hooks: baselineHooks,
                    timeProvider: new FakeTimeProvider(DeterministicClock));

                var convo = new InMemoryConversationManager();

                // Register the session conversation so the inactivity timer and distiller
                // can look it up by sessionId.
                var registry = services.GetRequiredService<IConversationManagerRegistry>()
                    as InMemoryConversationManagerRegistry;
                registry?.Register(sessionId, convo);

                return new MemoryChatSession(agent, userId, sessionId, convo);
            };
        }
    }

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the host, resets the Postgres schema, and starts all hosted services.
    /// Sets <see cref="PostgresSkipReason"/> and/or <see cref="LmStudioSkipReason"/> when
    /// the respective backend is unreachable; individual tests must check and call
    /// <c>Assert.Skip</c>.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        IConfiguration config = TestInfrastructure.BuildConfiguration();

        this.PostgresSkipReason =
            await TestInfrastructure.CheckPostgresAsync(config, CancellationToken.None);

        this.LmStudioSkipReason =
            await TestInfrastructure.CheckLmStudioAsync(config, CancellationToken.None);

        if (this.PostgresSkipReason is not null || this.LmStudioSkipReason is not null)
        {
            return;
        }

        this._dataSource = TestInfrastructure.BuildDataSource(config);

        string baseUrl = config[LmStudioBaseUrlKey]
            ?? throw new InvalidOperationException($"Configuration key '{LmStudioBaseUrlKey}' is required.");
        string apiKey = config[LmStudioApiKeyKey] ?? "lm-studio";
        string embeddingModel = config[LmStudioEmbeddingModelKey] ?? "local-embedding-model";

        // Detect the actual dimension emitted by the configured embedding model before
        // creating the schema. This prevents the vector(N) column mismatch that caused
        // UpsertAsync to fail with "expected 1536 dimensions, not 1024" when the loaded
        // model outputs a different size than the previous hardcoded constant.
        var probeEmbedder = new EmbeddingGenerator(new EmbeddingOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ModelId = embeddingModel,
        });
        this.EmbeddingDim = await TestInfrastructure.DetectEmbeddingDimAsync(
            probeEmbedder, CancellationToken.None);

        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, this.EmbeddingDim, CancellationToken.None);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        // Expose IConfiguration so the NewSession factory and other resolved components
        // can read LM Studio model names without a second BuildConfiguration() call.
        builder.Services.AddSingleton<IConfiguration>(config);

        // ── Postgres infrastructure ───────────────────────────────────────────
        builder.Services.AddSingleton(this._dataSource);
        builder.Services.AddSingleton<WatermarkRepository>();
        builder.Services.AddSingleton<DeadLetterRepository>();

        // ── Embedding generator ───────────────────────────────────────────────
        builder.Services.AddSingleton<IEmbeddingGenerator>(
            new EmbeddingGenerator(new EmbeddingOptions
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                ModelId = embeddingModel,
            }));

        // ── Memory store ──────────────────────────────────────────────────────
        builder.Services.AddSingleton<IMemoryStore>(sp =>
            new PostgresMemoryStore(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IEmbeddingGenerator>(),
                sp.GetRequiredService<IOptions<MemoryOptions>>(),
                sp.GetRequiredService<ILogger<PostgresMemoryStore>>()));

        // ── LLM clients ───────────────────────────────────────────────────────
        // IChatClient — consumed by ConsolidatorBackgroundService.
        builder.Services.AddSingleton<IChatClient>(_ =>
            new OpenAIClient(new LlmClientOptions
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
            }).CreateChatClient());

        // ILlmClientAdapter — consumed by DistillerBackgroundService (internal interface,
        // visible to this project via InternalsVisibleTo on Agency.Memory.Distiller).
        builder.Services.AddSingleton<ILlmClientAdapter>(_ =>
        {
            string chatModel = config[LmStudioChatModelKey] ?? "local-model";

            // Dedicated chat client for the distiller with SDK-level thinking suppression (TI-8.2),
            // kept separate from the consolidator/agent IChatClient above so only the distiller's
            // extraction calls suppress thinking. The EpisodeExtractionPrompt carries a matching
            // /no_think directive as a fallback for models/APIs that ignore the SDK option.
            IChatClient distillerLlm = new OpenAIClient(new LlmClientOptions
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                SuppressThinking = true,
            }).CreateChatClient();

            return new ChatClientLlmAdapter(distillerLlm, chatModel);
        });

        // ── AddAgencyMemory ───────────────────────────────────────────────────
        // Registers (from MemoryServiceCollectionExtensions):
        //   • IOptions<MemoryOptions>
        //   • IOptions<DistillerOptions>
        //   • IAsyncEventBus (InMemoryEventBus singleton)
        //   • ChannelSessionRegistry (singleton)
        //   • IConversationManagerRegistry → InMemoryConversationManagerRegistry (singleton)
        //   • InactivityTimerService (singleton + IHostedService)
        //   • DistillerBackgroundService (IHostedService)
        //   • AgentHooks (singleton — baseline retrieval + timer-restart hooks)
        builder.Services.AddAgencyMemory();

        // ── AddAgencyConsolidator ─────────────────────────────────────────────
        // Registers (from ConsolidatorServiceCollectionExtensions):
        //   • IOptions<ConsolidatorOptions>
        //   • ConsolidatorBackgroundService (IHostedService)
        // Requires IChatClient, IMemoryStore, IAsyncEventBus — all registered above.
        builder.Services.AddAgencyConsolidator();

        // NOTE (TI-1 wiring gap): HygieneSweeperBackgroundService is internal sealed with
        // no public DI registration extension in Agency.Memory.Hygiene. Cannot be wired
        // here without modifying production code. Group 4 tests use TimeShim + IMemoryStore
        // directly until a public AddAgencyHygiene() extension is added.

        this._host = builder.Build();
        await this._host.StartAsync();

        // Subscribe event-capture listeners on the live bus.
        IAsyncEventBus bus = this._host.Services.GetRequiredService<IAsyncEventBus>();

        this._eventSubscriptions.Add(bus.Subscribe<DistillationCompletedEvent>(async (evt, _) =>
        {
            this._capturedEvents.Add(evt);
            await Task.CompletedTask;
        }));

        this._eventSubscriptions.Add(bus.Subscribe<DistillationFailedEvent>(async (evt, _) =>
        {
            this._capturedEvents.Add(evt);
            await Task.CompletedTask;
        }));

        this._eventSubscriptions.Add(bus.Subscribe<ConsolidationCompletedEvent>(async (evt, _) =>
        {
            this._capturedEvents.Add(evt);
            await Task.CompletedTask;
        }));

        // Capture autonomous memory mutations so tests/hosts can surface them to the user (TI-8.3).
        this._eventSubscriptions.Add(bus.Subscribe<MemoryMutatedEvent>(async (evt, _) =>
        {
            this._capturedEvents.Add(evt);
            await Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Stops the host, unsubscribes event listeners, and disposes all managed resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable sub in this._eventSubscriptions)
        {
            sub.Dispose();
        }

        if (this._host is not null)
        {
            await this._host.StopAsync();
            this._host.Dispose();
        }

        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── Wait helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to the host's <see cref="IAsyncEventBus"/> and awaits a
    /// <see cref="DistillationCompletedEvent"/> for the given <paramref name="sessionId"/>,
    /// racing it against a <see cref="DistillationFailedEvent"/> for the same session.
    /// Default timeout is 60 seconds per Memory-TestPlan.md §2.1.
    /// </summary>
    /// <param name="sessionId">Session whose distillation completion to await.</param>
    /// <param name="userId">
    /// User identifier used to match the failure event. When <see langword="null"/> the first
    /// failure event for <paramref name="sessionId"/> is matched regardless of user.
    /// </param>
    /// <param name="timeout">
    /// Maximum wait duration. Defaults to 60 seconds when <see langword="null"/> or
    /// <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <param name="ct">Optional external cancellation token.</param>
    /// <returns>The matching <see cref="DistillationCompletedEvent"/> on success.</returns>
    /// <exception cref="DistillationFailedException">
    /// Thrown when a <see cref="DistillationFailedEvent"/> arrives before the completion event;
    /// the exception message carries the real failure reason.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when neither event arrives within <paramref name="timeout"/>.
    /// </exception>
    internal Task<DistillationCompletedEvent> WaitForDistillationAsync(
        string sessionId,
        string? userId = null,
        TimeSpan timeout = default,
        CancellationToken ct = default)
    {
        TimeSpan effective = timeout == default || timeout == TimeSpan.Zero
            ? TimeSpan.FromSeconds(60)
            : timeout;

        // Use a synthetic userId when not provided so the helper can match by sessionId only.
        // WaitForDistillationOrFailAsync requires a userId; pass the sessionId as a sentinel
        // when the caller does not know the userId, then post-filter by sessionId.
        if (userId is not null)
        {
            return TestInfrastructure.WaitForDistillationOrFailAsync(
                this.EventBus, userId, sessionId, effective, ct);
        }

        // Fallback: subscribe manually when no userId is available, using sessionId-only matching.
        return this.WaitForDistillationBySessionAsync(sessionId, effective, ct);
    }

    /// <summary>
    /// Internal fallback for <see cref="WaitForDistillationAsync"/> when no userId is known:
    /// races completion vs failure events matched by <paramref name="sessionId"/> alone.
    /// </summary>
    private async Task<DistillationCompletedEvent> WaitForDistillationBySessionAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var completedTcs =
            new TaskCompletionSource<DistillationCompletedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var failedTcs =
            new TaskCompletionSource<DistillationFailedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable completedSub =
            this.EventBus.Subscribe<DistillationCompletedEvent>(async (evt, _) =>
            {
                if (evt.SessionId == sessionId)
                {
                    completedTcs.TrySetResult(evt);
                }

                await Task.CompletedTask;
            });

        using IDisposable failedSub =
            this.EventBus.Subscribe<DistillationFailedEvent>(async (evt, _) =>
            {
                if (evt.SessionId == sessionId)
                {
                    failedTcs.TrySetResult(evt);
                }

                await Task.CompletedTask;
            });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        try
        {
            Task winner = await Task.WhenAny(
                completedTcs.Task,
                failedTcs.Task).WaitAsync(linkedCts.Token);

            if (winner == failedTcs.Task)
            {
                throw new DistillationFailedException(await failedTcs.Task);
            }

            return await completedTcs.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for distillation event for session '{sessionId}' after {timeout}.");
        }
    }

    /// <summary>
    /// Subscribes to the host's <see cref="IAsyncEventBus"/> and awaits a
    /// <see cref="ConsolidationCompletedEvent"/> for the given <paramref name="userId"/>.
    /// Default timeout is 60 seconds per Memory-TestPlan.md §2.1.
    /// </summary>
    /// <param name="userId">User whose consolidation completion to await.</param>
    /// <param name="timeout">
    /// Maximum wait duration. Defaults to 60 seconds when <see langword="null"/> or
    /// <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <param name="ct">Optional external cancellation token.</param>
    /// <returns>The matching <see cref="ConsolidationCompletedEvent"/>.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when the event does not arrive within <paramref name="timeout"/>.
    /// </exception>
    internal Task<ConsolidationCompletedEvent> WaitForConsolidationAsync(
        string userId,
        TimeSpan timeout = default,
        CancellationToken ct = default)
    {
        TimeSpan effective = timeout == default || timeout == TimeSpan.Zero
            ? TimeSpan.FromSeconds(60)
            : timeout;

        return TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
            this.EventBus,
            effective,
            predicate: e => e.UserId == userId,
            ct: ct);
    }
}

// ── Supporting types ─────────────────────────────────────────────────────────

/// <summary>
/// Minimal facade over a chat session, exposed by <see cref="MemoryE2EFixture.NewSession"/>.
/// </summary>
internal interface IChatSession
{
    /// <summary>
    /// Gets the <see cref="IConversationManager"/> for this session.
    /// The fixture registers this with <see cref="InMemoryConversationManagerRegistry"/>
    /// so the inactivity timer and distiller can resolve it by session ID.
    /// </summary>
    IConversationManager Conversation { get; }

    /// <summary>
    /// Sends <paramref name="userMessage"/> through the agent loop and streams the resulting
    /// <see cref="AgentEvent"/>s.
    /// </summary>
    /// <param name="userMessage">The user's turn text.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<AgentEvent> SendAsync(
        string userMessage,
        CancellationToken ct = default);
}

/// <summary>
/// Adapts an <see cref="Agent"/> and an <see cref="InMemoryConversationManager"/>
/// to the <see cref="IChatSession"/> interface.
/// </summary>
/// <remarks>
/// The <see cref="ChatSession"/> class manages its own <see cref="Context"/> (and therefore
/// its own conversation), so this adapter forwards calls to it while exposing the
/// externally-provided <paramref name="conversation"/> for registry registration.
/// </remarks>
internal sealed class MemoryChatSession : IChatSession
{
    private readonly ChatSession _inner;

    /// <summary>
    /// Initialises a new <see cref="MemoryChatSession"/>.
    /// </summary>
    /// <param name="agent">The agent driving the session.</param>
    /// <param name="userId">The user identifier (stored for context purposes).</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="conversation">
    /// The externally-created conversation manager registered with the distiller registry.
    /// </param>
    internal MemoryChatSession(
        Agent agent,
        string userId,
        string sessionId,
        InMemoryConversationManager conversation)
    {
        _ = userId;
        _ = sessionId;
        this.Conversation = conversation;
        this._inner = new ChatSession(agent, new AgentOptions());
    }

    /// <inheritdoc/>
    public IConversationManager Conversation { get; }

    /// <inheritdoc/>
    public IAsyncEnumerable<AgentEvent> SendAsync(
        string userMessage,
        CancellationToken ct = default) =>
        this._inner.SendAsync(userMessage, ct);
}
