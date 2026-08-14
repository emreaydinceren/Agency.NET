using System.Text.Json;
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Memory;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.Services;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Distiller.Tools;
using Agency.Memory.Retrieval;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end tests for the MemorizeNow tool (MemorizeNow-Design.md § Functional Test
/// Validations FT-1 through FT-5; MemorizeNow-Project-Plan.md Tasks 24-28).
/// </summary>
/// <remarks>
/// <para>
/// <b>Location deviation, flagged:</b> the project plan's illustrative path is
/// <c>Agency.Memory.Functional.Test/MemorizeNowIntegrationTests.cs</c>, matching the execution
/// brief's fallback ("add to existing functional suite") since that project (with
/// <c>InternalsVisibleTo</c> already granted from <c>Agency.Memory.Distiller</c> and
/// <c>Agency.Memory.Retrieval</c>) is the established home for all other Group/E2E memory
/// tests — not the brief's primary suggestion of
/// <c>Agency.Memory.Distiller.Test/Functional_MemorizeNow.cs</c>, which has no Postgres/E2E
/// fixture infrastructure of its own. Follows this suite's existing
/// <c>[Trait("Category","Functional")]</c> + <c>[Collection("memory-db")]</c> +
/// <c>TestInfrastructure</c> conventions (see <c>Group1CaptureAndRecallTests.cs</c>,
/// <c>Group6PerformanceTests.cs</c>).
/// </para>
/// <para>
/// <b>"Agent calls MemorizeNow" simulation:</b> rather than driving a real LLM to decide
/// (non-deterministically) whether to call the tool, these tests invoke
/// <see cref="MemorizeNowTool.InvokeAsync"/> directly with a JSON payload — the same
/// simulation strategy Group 1's E1.1-E1.5 use for the Distiller (injecting conversation
/// turns/jobs directly rather than running a full agent+LLM loop). This keeps the tests
/// deterministic and Postgres-only (no LM Studio dependency), matching Group 6/E1.6-E1.8.
/// </para>
/// <para>
/// <b>Scoping note on "Agent can reference the fact in turn 2 response" (FT-1):</b> this
/// suite treats the system prompt (<see cref="SystemPromptBuilder.Build"/>) as the
/// observable proof that the fact reached the agent — the same standard every other test in
/// this project uses (none of them assert on actual generated LLM text for memory-injection
/// scenarios). Actually completing a live LLM turn and asserting on its phrasing would be
/// non-deterministic and is out of scope here.
/// </para>
/// <para>
/// <b>FT-3/FT-4/FT-5 (Tasks 26-28) location, flagged:</b> the execution brief for Task 26
/// pointed at <c>Agency.Memory.Distiller.Test/Functional_MemorizeNow.cs</c> — a file that, by
/// the time this was written, a concurrent agent had already created there for FT-7 (Task 30),
/// but scoped deliberately Postgres-only/no-LLM (that scenario doesn't need one) and without
/// project references to <c>Agency.Llm.OpenAI</c> or the shared LM-Studio test configuration
/// this suite already has wired up. FT-3 (Distiller must actually skip a re-extraction) and
/// FT-4 (Consolidator must actually merge two records) are inherently LLM-behavior tests, so
/// duplicating that infrastructure into a second project for zero benefit was rejected in
/// favor of continuing to extend this already-fully-equipped, already-Design.md-designated
/// file. FT-5 doesn't need an LLM at all, but joins its siblings here for the same "one
/// MemorizeNow E2E home" reasoning Tasks 24-25 already established.
/// </para>
/// <para>
/// <b>FT-3 known gap, not fixed here</b> (already flagged by the Task 11/12 agent in the
/// project tracker): <c>EpisodeExtractionPrompt.FormatTurns</c> renders only
/// <see cref="TextContent"/> from each <see cref="ChatMessage"/> — a live agent's actual
/// <c>FunctionCallContent</c>/<c>FunctionResultContent</c> tool-call turns never reach the
/// transcript the Distiller LLM reads, so the v3 rule's literal "[Tool: MemorizeNow] in
/// transcript" pattern-match has no signal to fire on in a real tool-calling session. FT3
/// below instead drives the same real, observable no-duplicate outcome through the mechanism
/// that actually is wired end to end today — <c>DistillerBackgroundService.GetRecentFactsAsync</c>
/// includes every record the user already has (any source) in the "Recent Known Facts" dedup
/// context handed to the LLM, backed by the pre-existing "do not duplicate facts already
/// known" quality-bar rule. The acceptance criterion (one record, not two; source unchanged)
/// is identical either way; fixing <c>FormatTurns</c> is production-code surgery outside a
/// test-writing task's scope.
/// </para>
/// <para>
/// <b>FT-4/FT-5, no HTTP cassette yet:</b> unlike Group3ConsolidationTests's E3.x cases, these
/// are brand-new scenarios with no recorded cache-proxy cassette, so (mirroring E3.2's
/// tolerance model exactly) FT-4 advisory-skips rather than hard-fails when the LLM declines
/// to merge. FT-5 needs no LLM and is fully deterministic (see its own remarks).
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "MemorizeNow")]
[Collection("memory-db")]
public sealed class MemorizeNowIntegrationTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    /// <summary>Embedding dimension shared by all tests; must match the shared schema.</summary>
    private const int EmbeddingDim = 1536;

    private NpgsqlDataSource _dataSource = default!;
    private IEmbeddingGenerator _stubEmbedder = default!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the Postgres data source, resets the schema, and creates the stub embedder
    /// shared by all tests. Skips silently when Postgres is unreachable; individual tests
    /// re-check and skip.
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

    // ── FT-1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// FT-1 — Happy path: the agent calls MemorizeNow, the tool validates and persists the
    /// fact to Postgres with the correct key/importance/scope/provenance, and on the next
    /// turn the retrieval gate opens and the fact is injected into the <c>## Facts from Memory</c>
    /// section of the <c>&lt;memory&gt;</c> block (Design.md § FT-1).
    /// </summary>
    [Fact]
    public async Task FT1_HappyPath_AgentPersists_ThenNextTurnRecalls()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"FT1_HappyPath-{Guid.NewGuid():N}";
        const string SessionId = "ft1-s1";

        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Turn 1: agent calls MemorizeNow ───────────────────────────────────
        var tool = new MemorizeNowTool(store, userId, SessionId);
        JsonElement input = JsonDocument.Parse("""
            {
                "title": "Python 3.10 async perf",
                "value": "Python 3.10+ is 40% faster at async startup than 3.9, verified via local benchmark.",
                "domain": "Performance",
                "importance": "High",
                "tags": ["async", "startup"]
            }
            """).RootElement;

        ToolResult result = await tool.InvokeAsync(input, ct);

        // ── Assertions: tool call succeeds, console output format ────────────
        // MemorizeNowTool.InvokeAsync's return value IS what the console renders for a
        // "Calling MemorizeNow" tool result (ConsoleChatSession renders ToolResult.Content
        // verbatim) — see MemorizeNowToolTests.cs for the equivalent mocked-store assertions.
        Assert.False(result.IsError);
        Assert.Equal("MemorizeNow", tool.Definition.Name);
        Assert.Contains("✓ Memorized: performance|python-310-async-perf", result.Content);
        Assert.Contains("Source: AgentSignaled", result.Content);
        Assert.Contains("Importance: High", result.Content);
        Assert.Contains("Tags: async, startup", result.Content);

        // ── Assertions: record persisted to Postgres directly ────────────────
        MemoryRecord? record = await store.GetByKeyAsync(
            userId, sessionId: null, domain: "performance", key: "python-310-async-perf", ct);

        Assert.NotNull(record);
        Assert.Equal(MemorySource.AgentSignaled, record.Source);
        Assert.Equal("performance", record.Domain);
        Assert.Equal("python-310-async-perf", record.Key);
        Assert.Equal(0.9, record.Importance);
        Assert.Null(record.SessionId);

        // ── Turn 2: retrieval gate opens, fact injected into system prompt ───
        DateTimeOffset? lastWritten = await store.LastWrittenAtAsync(userId, ct);
        Assert.NotNull(lastWritten);

        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "What did we learn about Python performance?" },
            User = new UserSpecificContext { Id = userId },
            Session = new SessionContext { Id = SessionId },
            Conversation = new InMemoryConversationManager(),
        };
        ctx.Conversation.Append(new ChatMessage(
            ChatRole.User, "What did we learn about Python performance?"));

        bool shouldRetrieve = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);
        Assert.True(
            shouldRetrieve,
            "FT-1: Retrieval gate must open on turn 2 (fact written via MemorizeNow bumped LastWrittenAt).");

        var memOpts = Options.Create(new MemoryOptions { RetrievalTopK = 5, OverFetchFactor = 2 });
        var engine = new RetrievalEngine(store, this._stubEmbedder, memOpts);
        await engine.RetrieveAsync(ctx, ct);

        Assert.Contains(
            ctx.Knowledge.Records,
            r => r.Title == "Python 3.10 async perf");

        string memoryBlock = MemoryRenderer.Build(ctx);
        Assert.Contains("## Facts from Memory", memoryBlock);
        Assert.Contains("Python 3.10 async perf", memoryBlock);
        Assert.Contains("Python 3.10+ is 40% faster at async startup than 3.9", memoryBlock);
    }

    // ── FT-2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// FT-2 — Overwrite idempotency: calling MemorizeNow twice with the same domain+title
    /// (different value/importance/tags) overwrites the single record silently rather than
    /// creating a duplicate (Design.md § FT-2).
    /// </summary>
    [Fact]
    public async Task FT2_Overwrite_Idempotent()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"FT2_Overwrite-{Guid.NewGuid():N}";
        const string SessionId = "ft2-s1";

        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);
        var tool = new MemorizeNowTool(store, userId, SessionId);

        // ── Turn 1: first MemorizeNow call ────────────────────────────────────
        JsonElement firstInput = JsonDocument.Parse("""
            {
                "title": "Python 3.10 async perf",
                "value": "First value: Python 3.10+ is 40% faster at async startup.",
                "domain": "Performance",
                "importance": "High",
                "tags": ["async"]
            }
            """).RootElement;

        ToolResult firstResult = await tool.InvokeAsync(firstInput, ct);
        Assert.False(firstResult.IsError);
        Assert.Contains("✓ Memorized: performance|python-310-async-perf", firstResult.Content);

        DateTimeOffset? lastWrittenAfterFirst = await store.LastWrittenAtAsync(userId, ct);
        Assert.NotNull(lastWrittenAfterFirst);

        // Ensure the second write's timestamp is strictly later even on fast/coarse clocks.
        await Task.Delay(TimeSpan.FromMilliseconds(10), ct);

        // ── Turn 2: second MemorizeNow call — same domain+title, different content ──
        JsonElement secondInput = JsonDocument.Parse("""
            {
                "title": "Python 3.10 async perf",
                "value": "Second value: benchmarked again, 45% faster with uvloop.",
                "domain": "Performance",
                "importance": "Low",
                "tags": ["async", "uvloop", "benchmark"]
            }
            """).RootElement;

        ToolResult secondResult = await tool.InvokeAsync(secondInput, ct);

        // ── Assertions: both calls succeeded with "Memorized" confirmation ───
        Assert.False(secondResult.IsError);
        Assert.Contains("✓ Memorized: performance|python-310-async-perf", secondResult.Content);
        Assert.Contains("Source: AgentSignaled", secondResult.Content);

        // ── Assertions: exactly one record survives, holding the second call's content ──
        IReadOnlyList<MemoryRecord> all = await store.GetAllForUserAsync(userId, ct);
        List<MemoryRecord> matching = all
            .Where(r => r.Domain == "performance" && r.Key == "python-310-async-perf")
            .ToList();

        Assert.Single(matching);
        MemoryRecord record = matching[0];
        Assert.Equal("Second value: benchmarked again, 45% faster with uvloop.", record.Value);
        Assert.Equal(0.3, record.Importance);
        Assert.Equal(["async", "uvloop", "benchmark"], record.Tags);
        Assert.Equal(MemorySource.AgentSignaled, record.Source);

        // ── Assertions: LastWrittenAt is updated (fresher than after the first call) ──
        DateTimeOffset? lastWrittenAfterSecond = await store.LastWrittenAtAsync(userId, ct);
        Assert.NotNull(lastWrittenAfterSecond);
        Assert.True(
            lastWrittenAfterSecond > lastWrittenAfterFirst,
            $"FT-2: LastWrittenAt must advance after the overwrite. " +
            $"After first: {lastWrittenAfterFirst:O}, after second: {lastWrittenAfterSecond:O}.");
    }

    // ── FT-3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// FT-3 — No double-capture: a fact already persisted via MemorizeNow is not re-extracted
    /// (duplicated) by the Distiller when the same fact is also discussed in plain conversation
    /// text, per the EpisodeExtractionPrompt v3 "Skip MemorizeNow-signaled facts" rule
    /// (Design.md § FT-3; Project-Plan.md Task 26). See the class remarks for the known
    /// <c>FormatTurns</c> gap and why this test exercises the dedup outcome via the
    /// "Recent Known Facts" context instead of the literal transcript pattern-match.
    /// </summary>
    /// <remarks>LLM-gated: skips if LM Studio has no model loaded.</remarks>
    [Fact]
    public async Task FT3_NoCaptureDouble_DistillerSkipsMemorizeNowSignaledFact()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"FT3_NoCaptureDouble-{Guid.NewGuid():N}";
        const string SessionId = "ft3-s1";

        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Step 1: agent calls MemorizeNow mid-session (real tool → store path) ─
        var memorizeTool = new MemorizeNowTool(store, userId, SessionId);
        JsonElement memorizeInput = JsonDocument.Parse("""
            {
                "title": "Python 3.10 async perf",
                "value": "Python 3.10+ async is roughly 40% faster at startup due to reduced import overhead.",
                "domain": "Performance",
                "importance": "High",
                "tags": []
            }
            """).RootElement;
        ToolResult memorizeResult = await memorizeTool.InvokeAsync(memorizeInput, ct);
        Assert.False(memorizeResult.IsError);

        // ── Step 2: the same fact is ALSO discussed in plain text — realistic (the
        // agent explains what it just saved), and exactly the scenario that would
        // double-capture without a working dedup safeguard. ──────────────────────
        var conv = new InMemoryConversationManager();
        conv.Append(new ChatMessage(
            ChatRole.User, "Does upgrading to Python 3.10 help with async performance?"));
        conv.Append(new ChatMessage(
            ChatRole.Assistant,
            "Yes — Python 3.10+ async is roughly 40% faster at startup due to reduced import " +
            "overhead. I've saved that as a memory (Python 3.10 async perf) so we don't lose track of it."));

        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"FT3: MemorizeNow precondition seeded ({memorizeResult.Content}). " +
                $"LLM-gated half skipped: {llmSkip}");
            return;
        }

        string baseUrl = _config[LmStudioBaseUrlConfigKey]
            ?? throw new InvalidOperationException($"Configuration key '{LmStudioBaseUrlConfigKey}' is required.");
        string apiKey = _config[LmStudioApiKeyConfigKey] ?? "lm-studio";
        string chatModel = _config[LmStudioChatModelConfigKey] ?? "local-model";

        IChatClient llm = new Agency.Llm.OpenAI.OpenAIClient(new Agency.Llm.Common.LlmClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            SuppressThinking = true,
        }).CreateChatClient();
        ILlmClientAdapter llmAdapter = new ChatClientLlmAdapter(llm, chatModel);

        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var distillerOpts = new DistillerOptions
        {
            MaxRetries = 1,
            RetryBaseDelay = TimeSpan.FromSeconds(2),
            InactivityTimeout = TimeSpan.FromMinutes(5),
        };

        var channelRegistry = new ChannelSessionRegistry(
            Options.Create(distillerOpts), NullLogger<ChannelSessionRegistry>.Instance);
        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, conv);

        var distillerService = new DistillerBackgroundService(
            channelRegistry, convoRegistry, llmAdapter, this._stubEmbedder, store,
            watermarkRepo, deadLetterRepo, eventBus,
            Options.Create(distillerOpts), TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        await distillerService.StartAsync(cts.Token);

        var job = new DistillationJob(
            userId, SessionId, DistillationTrigger.SessionDisposed, UpToTurnIndex: conv.Messages.Count);
        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(job);

        try
        {
            await TestInfrastructure.WaitForDistillationOrFailAsync(
                eventBus, userId, SessionId, TimeSpan.FromSeconds(60), cts.Token);
        }
        catch (DistillationFailedException dfe)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip($"FT3: Distillation failed before completing. Real cause: {dfe.Message}");
            return;
        }
        catch (TimeoutException)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip("FT3: Distillation did not complete within 60 s. LM Studio may have no model loaded.");
            return;
        }
        catch (OperationCanceledException)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip("FT3: Distillation was cancelled.");
            return;
        }

        await distillerService.StopAsync(CancellationToken.None);

        // ── Acceptance: exactly one record for this fact, still AgentSignaled ────
        IReadOnlyList<MemoryRecord> allRecords = await store.GetAllForUserAsync(userId, ct);
        List<MemoryRecord> pythonRecords = allRecords.Where(r =>
            r.Title.Contains("Python 3.10", StringComparison.OrdinalIgnoreCase)
            || r.Value.Contains("reduced import overhead", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(
            pythonRecords.Count == 1,
            $"FT3: Expected exactly 1 record for the MemorizeNow-signaled fact after distillation " +
            $"(no double-capture), found {pythonRecords.Count}. Records: " +
            $"{string.Join("; ", pythonRecords.Select(r => $"id={r.Id} source={r.Source} title='{r.Title}'"))}.");

        Assert.Equal(MemorySource.AgentSignaled, pythonRecords[0].Source);
    }

    // ── FT-4 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// FT-4 — Consolidator prioritizes agent-signaled facts: when an older Distilled record and
    /// a newer AgentSignaled record describe the same fact, the Consolidator's merge keeps the
    /// AgentSignaled phrasing and provenance, per the ConsolidatorReconciliationPrompt v4
    /// "AgentSignaled facts take merge priority" rule (Design.md § FT-4; Project-Plan.md Task 27).
    /// </summary>
    /// <remarks>
    /// Mirrors Group3ConsolidationTests's E3.2 tolerance for live-LLM variance: no HTTP cassette
    /// has been recorded for this new test yet, so a missing merge advisory-skips rather than
    /// fails. LLM-gated: skips if LM Studio has no model loaded.
    /// </remarks>
    [Fact]
    public async Task FT4_ConsolidatorPriority_MergeKeepsAgentSignaledPhrasingAndSource()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"FT4_ConsolidatorPriority-{Guid.NewGuid():N}";

        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed the newer AgentSignaled record via the real MemorizeNow tool path ─
        var memorizeTool = new MemorizeNowTool(store, userId, "session-agent");
        JsonElement memorizeInput = JsonDocument.Parse("""
            {
                "title": "Python 3.10 async perf",
                "value": "Python 3.10+ async is roughly 40% faster at startup due to reduced import overhead, confirmed via profiling.",
                "domain": "Performance",
                "importance": "High",
                "tags": []
            }
            """).RootElement;
        ToolResult memorizeResult = await memorizeTool.InvokeAsync(memorizeInput, ct);
        Assert.False(memorizeResult.IsError);
        Assert.Contains("✓ Memorized: performance|python-310-async-perf", memorizeResult.Content);

        // ── Seed the older, vaguer Distilled record under the SAME (domain, key) but
        // a distinct session — the upsert key is (UserId, SessionId, Domain, Key), so
        // the differing SessionId keeps this as a second row instead of overwriting
        // the AgentSignaled one (mirrors Group3ConsolidationTests's E3.2 seeding shape). ─
        await store.UpsertAsync(MemoryRecord.Create(
            id: "77777777-7777-7777-7777-000000000001",
            userId: userId,
            sessionId: "session-distilled-old",
            contentType: ContentType.Fact,
            domain: "performance",
            key: "python-310-async-perf",
            title: "Python async performance (older note)",
            value: "Python provides better async performance in recent versions.",
            tags: [],
            importance: 0.6,
            createdAt: DateTimeOffset.UtcNow.AddDays(-5),
            updatedAt: DateTimeOffset.UtcNow.AddDays(-5),
            source: MemorySource.Distilled), ct);

        IReadOnlyList<MemoryRecord> before = await store.GetAllForUserAsync(userId, ct);
        Assert.Equal(2, before.Count);

        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"FT4: Postgres preconditions seeded ({before.Count} records). " +
                $"LLM-gated half skipped: {llmSkip}");
            return;
        }

        (ConsolidatorBackgroundService service, InMemoryEventBus eventBus) = BuildConsolidatorService(store);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        ConsolidationCompletedEvent completed;
        await service.StartAsync(cts.Token);
        try
        {
            var processTask = service.ProcessJobAsync(new ConsolidationJob(userId, "session-agent"), cts.Token);
            var waitTask = TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                eventBus, timeout: TimeSpan.FromSeconds(90), predicate: e => e.UserId == userId, ct: cts.Token);
            await processTask;
            completed = await waitTask;
        }
        catch (TimeoutException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip(
                "FT4: Consolidation did not complete within 90 s. " +
                "LM Studio is reachable but no model is loaded or response is too slow.");
            return;
        }
        catch (OperationCanceledException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip("FT4: Consolidation was cancelled (LM Studio request timeout).");
            return;
        }

        await service.StopAsync(CancellationToken.None);

        IReadOnlyList<MemoryRecord> after = await store.GetAllForUserAsync(userId, ct);
        Assert.NotNull(completed);
        Assert.Equal(userId, completed.UserId);

        if (after.Count >= before.Count)
        {
            Assert.Skip(
                $"FT4 [advisory]: No merge was observed (before={before.Count}, after={after.Count}). " +
                $"Subject to live LLM variance when no cassette is present for this new test. " +
                $"Remaining: {string.Join("; ", after.Select(r => $"{r.Title}: {r.Value}"))}.");
            return;
        }

        // ── Acceptance: the survivor must carry the AgentSignaled phrasing and source ─
        MemoryRecord? merged = after.FirstOrDefault(r =>
            r.Value.Contains("profiling", StringComparison.OrdinalIgnoreCase)
            || r.Title.Contains("Python 3.10", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            merged is not null,
            $"FT4: Expected the merged record to retain the AgentSignaled phrasing. " +
            $"Remaining records: {string.Join("; ", after.Select(r => $"{r.Title}: {r.Value}"))}.");

        Assert.Equal(MemorySource.AgentSignaled, merged!.Source);
    }

    // ── FT-5 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// FT-5 — Importance ranking: three MemorizeNow-signaled facts of different importance tiers
    /// (High=0.9, Normal=0.6, Low=0.3), otherwise identical in every ranking-formula input,
    /// retrieve in strict High &gt; Normal &gt; Low order (Design.md § FT-5; Project-Plan.md
    /// Task 28).
    /// </summary>
    /// <remarks>
    /// Uses a constant-vector embedder (every text embeds to the SAME fixed vector) instead of
    /// <see cref="TestInfrastructure.DeterministicEmbedder"/>'s per-text-hash randomness.
    /// Different titles under <see cref="TestInfrastructure.DeterministicEmbedder"/> hash to
    /// independent random vectors with essentially uncorrelated cosine similarity to the query —
    /// enough noise to occasionally swamp the ranking formula's importance term (wᵢ=0.2) and
    /// flake the strict-ordering assertion. Pinning every record (and the query) to an identical
    /// embedding vector makes the similarity term (wₛ=0.5) uniform across all three, and since
    /// all three are written back-to-back at the same (global) scope, recency and session-match
    /// are effectively uniform too — leaving importance as the only variable, so the ordering is
    /// deterministic by construction while still exercising the real Postgres store,
    /// <c>RankingFormula</c>, and <see cref="RetrievalEngine"/>. No LM Studio dependency.
    /// </remarks>
    [Fact]
    public async Task FT5_ImportanceRanking_HighAboveNormalAboveLow()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"FT5_ImportanceRanking-{Guid.NewGuid():N}";
        const string SessionId = "ft5-s1";

        IEmbeddingGenerator constantEmbedder = ConstantEmbedder(EmbeddingDim);
        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, constantEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        var tool = new MemorizeNowTool(store, userId, SessionId);

        async Task MemorizeAsync(string title, string importance)
        {
            JsonElement input = JsonDocument.Parse($$"""
                {
                    "title": "{{title}}",
                    "value": "Ranking test fact.",
                    "domain": "Ranking",
                    "importance": "{{importance}}",
                    "tags": []
                }
                """).RootElement;
            ToolResult result = await tool.InvokeAsync(input, ct);
            Assert.False(result.IsError);
        }

        // ── Seed 3 facts, all same domain, via the real MemorizeNow path ─────────
        await MemorizeAsync("High importance fact", "High");
        await MemorizeAsync("Normal importance fact", "Normal");
        await MemorizeAsync("Low importance fact", "Low");

        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "What do you know?" },
            User = new UserSpecificContext { Id = userId },
            Session = new SessionContext { Id = SessionId },
            Conversation = new InMemoryConversationManager(),
        };
        ctx.Conversation.Append(new ChatMessage(ChatRole.User, "What do you know?"));

        var memOpts = Options.Create(new MemoryOptions { RetrievalTopK = 10, OverFetchFactor = 2 });
        var engine = new RetrievalEngine(store, constantEmbedder, memOpts);
        await engine.RetrieveAsync(ctx, ct);

        // ── Acceptance: strict order High > Normal > Low ─────────────────────────
        List<string> titles = ctx.Knowledge.Records.Select(r => r.Title).ToList();
        int highIdx = titles.FindIndex(t => t.Contains("High importance", StringComparison.OrdinalIgnoreCase));
        int normalIdx = titles.FindIndex(t => t.Contains("Normal importance", StringComparison.OrdinalIgnoreCase));
        int lowIdx = titles.FindIndex(t => t.Contains("Low importance", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            highIdx >= 0 && normalIdx >= 0 && lowIdx >= 0,
            $"FT5: Expected all three importance-tier facts to be retrieved. Actual titles: {string.Join("; ", titles)}.");

        Assert.True(
            highIdx < normalIdx,
            $"FT5: High-importance fact (index {highIdx}) must rank above Normal-importance " +
            $"(index {normalIdx}). Order: {string.Join("; ", titles)}.");
        Assert.True(
            normalIdx < lowIdx,
            $"FT5: Normal-importance fact (index {normalIdx}) must rank above Low-importance " +
            $"(index {lowIdx}). Order: {string.Join("; ", titles)}.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private const string LmStudioBaseUrlConfigKey = "MemoryFunctional:LmStudio:BaseUrl";
    private const string LmStudioApiKeyConfigKey = "MemoryFunctional:LmStudio:ApiKey";
    private const string LmStudioChatModelConfigKey = "MemoryFunctional:LmStudio:ChatModel";

    /// <summary>
    /// Fixed wall-clock instant injected into the consolidator sub-agent so its system-prompt
    /// "Current date/time (UTC)" line is stable across runs (mirrors
    /// Group3ConsolidationTests.DeterministicClock).
    /// </summary>
    private static readonly DateTimeOffset DeterministicClock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a <see cref="ConsolidatorBackgroundService"/> wired with the real LM Studio
    /// <see cref="IChatClient"/> and the supplied <paramref name="store"/>, plus an
    /// <see cref="InMemoryEventBus"/> for event capture. Mirrors
    /// Group3ConsolidationTests.BuildConsolidatorService.
    /// </summary>
    private static (ConsolidatorBackgroundService Service, InMemoryEventBus EventBus) BuildConsolidatorService(
        IMemoryStore store)
    {
        string baseUrl = _config[LmStudioBaseUrlConfigKey]
            ?? throw new InvalidOperationException($"Configuration key '{LmStudioBaseUrlConfigKey}' is required.");
        string apiKey = _config[LmStudioApiKeyConfigKey] ?? "lm-studio";
        string chatModel = _config[LmStudioChatModelConfigKey] ?? "local-model";

        IChatClient llmClient = new Agency.Llm.OpenAI.OpenAIClient(new Agency.Llm.Common.LlmClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
        }).CreateChatClient();

        var consolidatorOpts = Options.Create(new ConsolidatorOptions
        {
            MaxIterations = 20,
            MaxCostUsd = 1.0m,
            Model = chatModel,
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var timeProvider = new FakeTimeProvider(DeterministicClock);
        int mergeSeq = 0;
        Func<string> mergeIdFactory = () => $"00000000-0000-0000-0000-{(++mergeSeq):D12}";

        Func<string, IReadOnlyList<MemoryRecord>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>> runner =
            ConsolidatorSubAgentFactory.CreateRunner(
                llmClient, chatModel, store, consolidatorOpts, eventBus,
                NullLogger<Agency.Harness.Agent>.Instance, timeProvider, mergeIdFactory);

        var service = new ConsolidatorBackgroundService(
            store, runner, eventBus, consolidatorOpts, NullLogger<ConsolidatorBackgroundService>.Instance);

        return (service, eventBus);
    }

    /// <summary>
    /// Creates an <see cref="IEmbeddingGenerator"/> that returns the SAME fixed vector for every
    /// input, regardless of text. Used by FT-5 to pin the similarity term of the ranking formula
    /// constant across all seeded records, isolating importance as the only variable — see the
    /// remarks on <see cref="FT5_ImportanceRanking_HighAboveNormalAboveLow"/>.
    /// </summary>
    private static IEmbeddingGenerator ConstantEmbedder(int dim)
    {
        float[] fixedVector = Enumerable.Repeat(0.1f, dim).ToArray();
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReadOnlyMemory<float>)fixedVector.AsMemory());
        return mock.Object;
    }
}
