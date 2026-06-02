using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Common.Hooks;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Functional.Test.Infrastructure;
using Agency.Memory.Retrieval;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end Group 7 — Hook Composition tests.
/// Verifies the hook ordering semantics described in Memory-Specifications.md §6.5:
/// the default baseline-first ordering and the <c>ComposeBefore</c> escape hatch
/// (Memory-TestPlan.md §3, Group 7).
/// </summary>
/// <remarks>
/// All tests carry <c>[Trait("Category","Functional")]</c> and
/// <c>[Trait("Group","HookComposition")]</c> so they can be run in isolation:
/// <code>
/// dotnet test src/Memory/Agency.Memory.Functional.Test
///     --filter "Category=Functional&amp;Group=HookComposition"
/// </code>
/// Each test uses a unique <c>userId</c> of the form <c>{TestName}-{Guid}</c>
/// so residue from a failed run cannot poison subsequent runs.
///
/// These tests are fully deterministic — they do NOT require LM Studio.
/// Only Postgres is required (for the real <see cref="IMemoryStore"/>).
/// Schema dim is fixed at 1 536. Do NOT call
/// <see cref="TestInfrastructure.ResetSchemaAsync"/> with a different dimension
/// from within this class.
///
/// The tests assert hook-ordering causality by invoking the composed
/// <see cref="AgentHooks.OnPreIteration"/> delegate directly on a <see cref="Context"/>
/// rather than going through a full <c>Agent</c> loop, eliminating LLM variance
/// from the observable.
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "HookComposition")]
[Collection("memory-db")]
public sealed class Group7HookCompositionTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    /// <summary>Embedding dimension shared by all tests; must match the shared schema.</summary>
    private const int EmbeddingDim = 1536;

    private NpgsqlDataSource _dataSource = default!;
    private IEmbeddingGenerator _stubEmbedder = default!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the Postgres data source, resets the schema to a clean state at
    /// the standard 1 536-dim column, and creates the stub embedder shared by all tests.
    /// Skips silently when Postgres is unreachable; individual tests re-check and skip.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);

        if (pgSkip is not null)
        {
            return;
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, EmbeddingDim, TestContext.Current.CancellationToken);

        this._stubEmbedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
    }

    /// <summary>Disposes the Postgres data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── E7.1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E7.1 — Verifies the default hook ordering (Spec §6.5): a user hook composed
    /// <b>after</b> the baseline memory hook via <see cref="AgentHooksExtensions.Compose"/>
    /// runs after the retrieval engine has populated <see cref="Context.Knowledge"/> and
    /// <see cref="Context.Memory"/>, and therefore observes a non-empty enriched context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setup: seed one <see cref="ContentType.Fact"/> record and one
    /// <see cref="ContentType.Memory"/> record for the test user so that retrieval
    /// will return results. The context is fresh (<see cref="Context.MemoryLastRetrievedAt"/>
    /// is <see langword="null"/>), guaranteeing the retrieval gate opens.
    /// </para>
    /// <para>
    /// Composed hook: <c>baseline.Compose(captureHook)</c> — baseline runs first,
    /// captureHook runs second (sees enriched context).
    /// </para>
    /// <para>
    /// Acceptance: the snapshot captured by the user hook has at least one record in
    /// <see cref="KnowledgeContext.Records"/> or <see cref="MemoryContext.Records"/>,
    /// proving it ran after the retrieval engine wrote into the context.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UserHookComposedAfterBaseline_SeesEnrichedContextKnowledgeAndMemory()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E71_AfterBaseline-{Guid.NewGuid():N}";
        const string SessionId = "e71-s1";

        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed records so retrieval has something to return ─────────────────
        await store.UpsertAsync(MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: "Language",
            title: "Preferred language",
            value: "User prefers Rust for systems programming.",
            tags: ["rust", "language"],
            importance: 0.8,
            createdAt: DateTimeOffset.UtcNow.AddDays(-1),
            updatedAt: DateTimeOffset.UtcNow.AddDays(-1)),
            ct);

        await store.UpsertAsync(MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Memory,
            domain: "Debugging",
            key: "TcpTimeout",
            title: "TCP timeout debugging episode",
            value: "Resolved a TCP connection timeout by adjusting socket keepalive settings.",
            tags: ["tcp", "debugging"],
            importance: 0.7,
            createdAt: DateTimeOffset.UtcNow.AddDays(-2),
            updatedAt: DateTimeOffset.UtcNow.AddDays(-2)),
            ct);

        // ── Build baseline hooks with a real retrieval engine ─────────────────
        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 5,
            OverFetchFactor = 2,
        });
        var engine = new RetrievalEngine(store, this._stubEmbedder, memOpts);

        Func<Context, CancellationToken, Task> retrievalCallback = async (ctx, token) =>
        {
            bool shouldRetrieve = await RetrievalGate.ShouldRetrieveAsync(ctx, store, token)
                .ConfigureAwait(false);
            if (shouldRetrieve)
            {
                await engine.RetrieveAsync(ctx, token).ConfigureAwait(false);
            }
        };

        AgentHooks baseline = MemoryHookFactory.Build(
            retrievalCallback,
            timerRestartCallback: (_, _) => Task.CompletedTask);

        // ── Build capture hook that snapshots context when it runs ────────────
        var captureHook = new ContextOrderingCaptureHook();
        AgentHooks composed = baseline.Compose(captureHook.AsHooks(SessionId));

        // ── Build a fresh context — gate must open (MemoryLastRetrievedAt is null) ──
        Context ctx2 = BuildContext(userId, "What is my preferred language?");

        // ── Invoke the composed OnPreIteration: baseline runs first, then capture hook ──
        await composed.OnPreIteration!(ctx2, ct);

        // ── Acceptance ─────────────────────────────────────────────────────────
        // The capture hook ran after the baseline; it must have seen enriched context.
        ContextOrderingSnapshot? snapshot = captureHook.GetSnapshot(SessionId);

        Assert.NotNull(snapshot);

        bool hasEnrichedRecords =
            (snapshot.KnowledgeRecordCount + snapshot.MemoryRecordCount) > 0;

        Assert.True(
            hasEnrichedRecords,
            $"E7.1: User hook composed after baseline must see a non-empty enriched context. " +
            $"Knowledge.Records.Count={snapshot.KnowledgeRecordCount}, " +
            $"Memory.Records.Count={snapshot.MemoryRecordCount}. " +
            $"If both are 0 the baseline retrieval hook did not run before the user hook " +
            $"(composition ordering bug).");
    }

    // ── E7.2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E7.2 — Verifies the <c>ComposeBefore</c> escape hatch (Spec §6.5): a user hook
    /// composed <b>before</b> the baseline via <see cref="AgentHooksExtensions.ComposeBefore"/>
    /// runs before the retrieval engine has populated <see cref="Context.Knowledge"/> and
    /// <see cref="Context.Memory"/>, and therefore observes an empty, un-enriched context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setup: same as E7.1 — seed records so that if retrieval were to run first the
    /// context would be non-empty. Using <c>ComposeBefore</c> reverses the order.
    /// </para>
    /// <para>
    /// Composed hook: <c>baseline.ComposeBefore(captureHook)</c> — captureHook runs
    /// first (sees empty context), baseline runs second (populates context).
    /// </para>
    /// <para>
    /// Acceptance: the snapshot captured by the user hook has zero records in both
    /// <see cref="KnowledgeContext.Records"/> and <see cref="MemoryContext.Records"/>,
    /// proving it ran before the retrieval engine wrote into the context.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ComposeBefore_UserHookSeesEmptyContext_BeforeRetrieval()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E72_BeforeBaseline-{Guid.NewGuid():N}";
        const string SessionId = "e72-s1";

        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed records — same as E7.1 — so retrieval would enrich context if it ran first ──
        await store.UpsertAsync(MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: "Editor",
            title: "Preferred editor",
            value: "User prefers Neovim for all editing tasks.",
            tags: ["neovim", "editor"],
            importance: 0.8,
            createdAt: DateTimeOffset.UtcNow.AddDays(-1),
            updatedAt: DateTimeOffset.UtcNow.AddDays(-1)),
            ct);

        await store.UpsertAsync(MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Memory,
            domain: "Debugging",
            key: "NullRef",
            title: "NullReferenceException debugging episode",
            value: "Resolved a NullReferenceException by adding null checks at the service boundary.",
            tags: ["null", "debugging"],
            importance: 0.6,
            createdAt: DateTimeOffset.UtcNow.AddDays(-3),
            updatedAt: DateTimeOffset.UtcNow.AddDays(-3)),
            ct);

        // ── Build baseline hooks with a real retrieval engine ─────────────────
        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 5,
            OverFetchFactor = 2,
        });
        var engine = new RetrievalEngine(store, this._stubEmbedder, memOpts);

        Func<Context, CancellationToken, Task> retrievalCallback = async (ctx, token) =>
        {
            bool shouldRetrieve = await RetrievalGate.ShouldRetrieveAsync(ctx, store, token)
                .ConfigureAwait(false);
            if (shouldRetrieve)
            {
                await engine.RetrieveAsync(ctx, token).ConfigureAwait(false);
            }
        };

        AgentHooks baseline = MemoryHookFactory.Build(
            retrievalCallback,
            timerRestartCallback: (_, _) => Task.CompletedTask);

        // ── Build capture hook and compose it BEFORE the baseline ─────────────
        var captureHook = new ContextOrderingCaptureHook();
        AgentHooks composed = baseline.ComposeBefore(captureHook.AsHooks(SessionId));

        // ── Build a fresh context — gate would open if retrieval ran first ─────
        Context ctx2 = BuildContext(userId, "Which editor should I use?");

        // ── Invoke the composed OnPreIteration: capture hook runs first, then baseline ──
        await composed.OnPreIteration!(ctx2, ct);

        // ── Acceptance ─────────────────────────────────────────────────────────
        // The capture hook ran before the baseline; it must have seen an empty context.
        ContextOrderingSnapshot? snapshot = captureHook.GetSnapshot(SessionId);

        Assert.NotNull(snapshot);

        Assert.True(
            snapshot.KnowledgeRecordCount == 0,
            $"E7.2: User hook composed before baseline must see empty Knowledge.Records " +
            $"(retrieval has not run yet). " +
            $"Actual Knowledge.Records.Count={snapshot.KnowledgeRecordCount}. " +
            $"If non-zero the baseline retrieval hook ran before the user hook — " +
            $"ComposeBefore escape hatch is broken.");

        Assert.True(
            snapshot.MemoryRecordCount == 0,
            $"E7.2: User hook composed before baseline must see empty Memory.Records " +
            $"(retrieval has not run yet). " +
            $"Actual Memory.Records.Count={snapshot.MemoryRecordCount}. " +
            $"If non-zero the baseline retrieval hook ran before the user hook — " +
            $"ComposeBefore escape hatch is broken.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fresh <see cref="Context"/> with the given <paramref name="userId"/>
    /// and prompt, no prior retrieval timestamp, and a single user message in the
    /// conversation so <see cref="RetrievalEngine"/> can derive a query from it.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="prompt">The user prompt text.</param>
    /// <returns>A new <see cref="Context"/> ready for hook invocation.</returns>
    private static Context BuildContext(string userId, string prompt)
    {
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = prompt },
            User = new UserSpecificContext { Id = userId },
            Conversation = new InMemoryConversationManager(),
        };

        ctx.Conversation.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User,
            prompt));

        return ctx;
    }
}

/// <summary>
/// A minimal test hook that snapshots the <see cref="KnowledgeContext.Records"/> count and
/// <see cref="MemoryContext.Records"/> count from <see cref="Context"/> at the moment it fires.
/// Used by Group 7 tests to observe whether the hook ran before or after the baseline
/// retrieval hook.
/// </summary>
/// <remarks>
/// Unlike <see cref="SystemPromptCaptureHook"/>, this hook captures only the record counts
/// rather than full context objects, keeping the observable minimal and the intent clear:
/// the only thing under test is whether the context was already enriched when the hook ran.
/// </remarks>
internal sealed class ContextOrderingCaptureHook
{
    private volatile ContextOrderingSnapshot? _snapshot;

    /// <summary>
    /// Gets the captured snapshot, or <see langword="null"/> if the hook has not fired yet.
    /// </summary>
    /// <param name="sessionId">
    /// The session identifier. Included for symmetry with other capture hooks in this project;
    /// currently only one snapshot per hook instance is retained.
    /// </param>
    /// <returns>The captured snapshot, or <see langword="null"/>.</returns>
    internal ContextOrderingSnapshot? GetSnapshot(string sessionId)
    {
        _ = sessionId;
        return this._snapshot;
    }

    /// <summary>
    /// Returns an <see cref="AgentHooks"/> instance with <see cref="AgentHooks.OnPreIteration"/>
    /// wired to snapshot the Knowledge and Memory record counts for the given
    /// <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">The session identifier stored in the snapshot.</param>
    /// <returns>
    /// An <see cref="AgentHooks"/> with only <see cref="AgentHooks.OnPreIteration"/> set.
    /// </returns>
    internal AgentHooks AsHooks(string sessionId) =>
        new()
        {
            OnPreIteration = (ctx, _) =>
            {
                this._snapshot = new ContextOrderingSnapshot(
                    SessionId: sessionId,
                    KnowledgeRecordCount: ctx.Knowledge.Records.Count,
                    MemoryRecordCount: ctx.Memory.Records.Count);

                return Task.CompletedTask;
            },
        };
}

/// <summary>
/// An immutable snapshot of the Knowledge and Memory record counts captured by
/// <see cref="ContextOrderingCaptureHook"/> at the moment the hook fired.
/// </summary>
/// <param name="SessionId">The session identifier.</param>
/// <param name="KnowledgeRecordCount">
/// The number of <see cref="KnowledgeContext.Records"/> observed at snapshot time.
/// </param>
/// <param name="MemoryRecordCount">
/// The number of <see cref="MemoryContext.Records"/> observed at snapshot time.
/// </param>
internal sealed record ContextOrderingSnapshot(
    string SessionId,
    int KnowledgeRecordCount,
    int MemoryRecordCount);
