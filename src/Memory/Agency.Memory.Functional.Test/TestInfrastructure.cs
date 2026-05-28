using Agency.Memory.Common.Events;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// Thrown by <see cref="TestInfrastructure.WaitForDistillationOrFailAsync"/> when the
/// distiller emits <see cref="DistillationFailedEvent"/> before completing successfully.
/// Carries the <see cref="DistillationFailedEvent.Reason"/> text so the test can
/// surface the real cause instead of a misleading timeout message.
/// </summary>
internal sealed class DistillationFailedException : Exception
{
    /// <summary>Gets the underlying <see cref="DistillationFailedEvent"/>.</summary>
    internal DistillationFailedEvent Event { get; }

    /// <summary>
    /// Initialises the exception with the failed event.
    /// </summary>
    /// <param name="failedEvent">The event that caused the failure.</param>
    internal DistillationFailedException(DistillationFailedEvent failedEvent)
        : base(
            $"Distillation failed for session '{failedEvent.SessionId}' " +
            $"(user '{failedEvent.UserId}'): {failedEvent.Reason} " +
            $"[dead-lettered={failedEvent.DeadLettered}]")
    {
        this.Event = failedEvent;
    }
}

/// <summary>
/// Shared test infrastructure for end-to-end functional tests.
/// </summary>
/// <remarks>
/// Provides connectivity checks, Postgres data-source factories, schema helpers,
/// and stub implementations for components that do not need real network calls.
/// </remarks>
internal static class TestInfrastructure
{
    /// <summary>
    /// Serialises schema-reset calls to avoid a pgvector <c>pg_type</c> duplicate-key race
    /// when multiple test classes run concurrently and each tries to
    /// <c>CREATE EXTENSION IF NOT EXISTS vector</c> at the same time.
    /// </summary>
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);
    private const string LmStudioBaseUrlConfigKey = "MemoryFunctional:LmStudio:BaseUrl";
    private const string PostgresConnectionStringConfigKey = "ConnectionStrings:PostgreSql";

    /// <summary>
    /// Per-project Postgres schema. Isolating this assembly's tables here prevents schema-reset
    /// races against <c>Agency.Memory.Sql.Postgres.Test</c> (which targets the same database but
    /// uses its own schema) when <c>dotnet test</c> executes the two assemblies in parallel.
    /// </summary>
    private const string TestSchema = "mem_func_test";

    // ── Configuration ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds shared <see cref="IConfiguration"/> from appsettings.json, user secrets, and
    /// environment variables.
    /// </summary>
    internal static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddUserSecrets("AgencySecrets")
            .AddEnvironmentVariables()
            .Build();

    // ── Skip helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to open a Postgres connection. Returns a skip-reason string on failure,
    /// or <see langword="null"/> if reachable.
    /// </summary>
    internal static async Task<string?> CheckPostgresAsync(IConfiguration config, CancellationToken ct)
    {
        string? cs = config[PostgresConnectionStringConfigKey];
        if (string.IsNullOrWhiteSpace(cs))
        {
            return "Postgres connection string not configured.";
        }

        try
        {
            await using NpgsqlDataSource ds = BuildDataSource(config);
            await using var conn = await ds.OpenConnectionAsync(ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"Postgres unreachable: {ex.Message}";
        }
    }

    /// <summary>
    /// Issues a lightweight HTTP probe to LM Studio. Returns a skip-reason on failure,
    /// or <see langword="null"/> if reachable.
    /// </summary>
    internal static async Task<string?> CheckLmStudioAsync(IConfiguration config, CancellationToken ct)
    {
        string? baseUrl = config[LmStudioBaseUrlConfigKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "LM Studio base URL not configured.";
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string url = baseUrl.TrimEnd('/') + "/models";
            using var response = await http.GetAsync(url, ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"LM Studio unreachable ({baseUrl}): {ex.Message}";
        }
    }

    // ── Postgres helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="NpgsqlDataSource"/> from configuration.
    /// </summary>
    /// <remarks>
    /// Each physical connection is initialised to point at <see cref="TestSchema"/> so that all
    /// table operations are isolated from the sibling <c>Agency.Memory.Sql.Postgres.Test</c>
    /// assembly. <c>public</c> stays on the search_path so the <c>vector</c> extension type is
    /// still resolvable.
    /// </remarks>
    internal static NpgsqlDataSource BuildDataSource(IConfiguration config)
    {
        string cs = config[PostgresConnectionStringConfigKey]
            ?? throw new InvalidOperationException("Postgres connection string not configured.");

        // Preserve the SET search_path issued by the physical-connection initializer across
        // pool re-acquires. Without this Npgsql resets the session (DISCARD ALL) on close
        // and our schema isolation would only hold for the first command on each connection.
        var csb = new NpgsqlConnectionStringBuilder(cs) { NoResetOnClose = true };
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        builder.UseVector();
        ConfigureSchemaIsolation(builder, TestSchema);
        return builder.Build();
    }

    /// <summary>
    /// Wires a physical-connection initialiser that pins every new connection to
    /// <paramref name="schemaName"/>. Mirrors the helper used by
    /// <c>Agency.Memory.Sql.Postgres.Test</c>; the schema is created on first use.
    /// </summary>
    private static void ConfigureSchemaIsolation(NpgsqlDataSourceBuilder builder, string schemaName)
    {
        string initSql = $"CREATE SCHEMA IF NOT EXISTS {schemaName}; SET search_path TO {schemaName}, public;";

        builder.UsePhysicalConnectionInitializer(
            conn =>
            {
                using var cmd = new NpgsqlCommand(initSql, conn);
                cmd.ExecuteNonQuery();
            },
            async conn =>
            {
                await using var cmd = new NpgsqlCommand(initSql, conn);
                await cmd.ExecuteNonQueryAsync();
            });
    }

    /// <summary>
    /// Drops and re-creates the <c>records</c> table at the specified embedding dimension,
    /// then truncates supporting tables. Call in <c>InitializeAsync()</c> to get a clean slate.
    /// </summary>
    /// <remarks>
    /// Internally serialised via a <see cref="SemaphoreSlim"/> to avoid a pgvector
    /// <c>pg_type</c> duplicate-key race when multiple test classes run concurrently.
    /// </remarks>
    internal static async Task ResetSchemaAsync(NpgsqlDataSource ds, int dim, CancellationToken ct)
    {
        await _schemaLock.WaitAsync(ct);
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var dropCmd = new NpgsqlCommand(
                "DROP TABLE IF EXISTS records CASCADE;", (NpgsqlConnection)conn);
            await dropCmd.ExecuteNonQueryAsync(ct);

            await new MemorySchemaInitializer(ds).InitializeAsync(dim, ct);

            await using var truncCmd = new NpgsqlCommand(
                "TRUNCATE TABLE watermarks, dead_letter, user_state;", (NpgsqlConnection)conn);
            await truncCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    // ── Stub factories ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a deterministic <see cref="IEmbeddingGenerator"/> that produces vectors of
    /// <paramref name="dim"/> dimensions seeded by the hash of the input text.
    /// Suitable for tests that need consistent, fast embeddings without a real embedding service.
    /// </summary>
    internal static IEmbeddingGenerator DeterministicEmbedder(int dim)
    {
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((input, _) =>
            {
                var rng = new Random(input.GetHashCode());
                float[] arr = new float[dim];
                for (int i = 0; i < dim; i++)
                {
                    arr[i] = (float)rng.NextDouble();
                }

                return Task.FromResult((ReadOnlyMemory<float>)arr.AsMemory());
            });
        return mock.Object;
    }

    /// <summary>
    /// Creates a stub <see cref="IChatClient"/> that returns a fixed text response.
    /// </summary>
    internal static IChatClient StubChatClient(string response)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
                new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
        return mock.Object;
    }

    /// <summary>
    /// Waits for an event of type <typeparamref name="T"/> on the <paramref name="bus"/> with
    /// a timeout. Throws <see cref="TimeoutException"/> if the event is not received.
    /// </summary>
    internal static async Task<T> WaitForEventAsync<T>(
        IAsyncEventBus bus,
        TimeSpan timeout,
        Func<T, bool>? predicate = null,
        CancellationToken ct = default)
        where T : Agency.Agentic.AgentEvent
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable sub = bus.Subscribe<T>(async (evt, _) =>
        {
            if (predicate is null || predicate(evt))
            {
                tcs.TrySetResult(evt);
            }

            await Task.CompletedTask;
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for event {typeof(T).Name} after {timeout}.");
        }
    }

    /// <summary>
    /// Races a <see cref="DistillationCompletedEvent"/> against a
    /// <see cref="DistillationFailedEvent"/> for the given session on <paramref name="bus"/>.
    /// </summary>
    /// <param name="bus">The event bus to subscribe to.</param>
    /// <param name="userId">The user identifier to match.</param>
    /// <param name="sessionId">The session identifier to match.</param>
    /// <param name="timeout">Maximum wait duration before throwing <see cref="TimeoutException"/>.</param>
    /// <param name="ct">Optional external cancellation token.</param>
    /// <returns>The matching <see cref="DistillationCompletedEvent"/> on success.</returns>
    /// <exception cref="DistillationFailedException">
    /// Thrown when <see cref="DistillationFailedEvent"/> arrives first; the exception carries
    /// the real failure reason so the test can surface it directly.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when neither event arrives within <paramref name="timeout"/>.
    /// </exception>
    internal static async Task<DistillationCompletedEvent> WaitForDistillationOrFailAsync(
        IAsyncEventBus bus,
        string userId,
        string sessionId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var completedTcs =
            new TaskCompletionSource<DistillationCompletedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var failedTcs =
            new TaskCompletionSource<DistillationFailedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable completedSub =
            bus.Subscribe<DistillationCompletedEvent>(async (evt, _) =>
            {
                if (evt.UserId == userId && evt.SessionId == sessionId)
                {
                    completedTcs.TrySetResult(evt);
                }

                await Task.CompletedTask;
            });

        using IDisposable failedSub =
            bus.Subscribe<DistillationFailedEvent>(async (evt, _) =>
            {
                if (evt.UserId == userId && evt.SessionId == sessionId)
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
    /// Probes the given <paramref name="embedder"/> with a short sentinel string and
    /// returns the number of dimensions in the returned vector.
    /// </summary>
    /// <param name="embedder">The embedding generator to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The detected dimension count, or <c>1536</c> as a safe fallback if the embedder
    /// is unreachable or returns an empty vector.
    /// </returns>
    /// <remarks>
    /// Call this once per test class initialisation after the LM Studio reachability check
    /// has passed. If the embedder is transiently unreachable the method returns the fallback
    /// so that fixture init never hard-fails; the LM Studio skip-reason check will handle
    /// skipping individual tests.
    /// </remarks>
    internal static async Task<int> DetectEmbeddingDimAsync(
        IEmbeddingGenerator embedder,
        CancellationToken ct = default)
    {
        const int FallbackDim = 1536;
        try
        {
            ReadOnlyMemory<float> probe = await embedder.GenerateEmbeddingAsync("dim-probe", ct);
            return probe.Length > 0 ? probe.Length : FallbackDim;
        }
        catch
        {
            return FallbackDim;
        }
    }

    // ── Memory store builder ─────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="PostgresMemoryStore"/> using the given data source and a
    /// deterministic embedder.
    /// </summary>
    internal static PostgresMemoryStore BuildMemoryStore(
        NpgsqlDataSource ds,
        IEmbeddingGenerator embedder,
        Microsoft.Extensions.Logging.ILogger<PostgresMemoryStore> logger)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MemoryOptions());
        return new PostgresMemoryStore(ds, embedder, opts, logger);
    }
}

/// <summary>
/// A stub <see cref="ILlmClientAdapter"/> that always returns a pre-configured response string.
/// Allows the Distiller to be driven without a real LLM.
/// </summary>
internal sealed class StubLlmAdapter : ILlmClientAdapter
{
    private readonly Func<string, string> _responseFactory;

    /// <summary>
    /// Initialises the stub with a fixed response for all requests.
    /// </summary>
    /// <param name="response">The response JSON to return for every call.</param>
    internal StubLlmAdapter(string response)
        : this(_ => response)
    {
    }

    /// <summary>
    /// Initialises the stub with a factory that produces responses per-prompt.
    /// </summary>
    /// <param name="factory">Function that receives the prompt and returns a response string.</param>
    internal StubLlmAdapter(Func<string, string> factory)
    {
        this._responseFactory = factory;
    }

    /// <inheritdoc/>
    public Task<string> SendAsync(string prompt, CancellationToken ct = default) =>
        Task.FromResult(this._responseFactory(prompt));
}

/// <summary>
/// A stub <see cref="ILlmClientAdapter"/> that throws on the first call and then
/// returns a fixed response on subsequent calls. Used by G.4 to simulate a crash.
/// </summary>
internal sealed class ThrowOnceLlmAdapter : ILlmClientAdapter
{
    private int _callCount;
    private readonly string _successResponse;

    /// <summary>
    /// Initialises the adapter.
    /// </summary>
    /// <param name="successResponse">The JSON to return after the first (throwing) call.</param>
    internal ThrowOnceLlmAdapter(string successResponse)
    {
        this._successResponse = successResponse;
    }

    /// <summary>Gets the number of calls made to <see cref="SendAsync"/>.</summary>
    internal int CallCount => this._callCount;

    /// <inheritdoc/>
    public Task<string> SendAsync(string prompt, CancellationToken ct = default)
    {
        int call = System.Threading.Interlocked.Increment(ref this._callCount);
        if (call == 1)
        {
            throw new HttpRequestException("Simulated LLM transient failure (call 1).", null,
                System.Net.HttpStatusCode.ServiceUnavailable);
        }

        return Task.FromResult(this._successResponse);
    }
}
