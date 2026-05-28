using Agency.Agentic;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Distiller.Test.Stubs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Tests for <see cref="DistillerBackgroundService"/> happy-path and watermark idempotency
/// (Spec §6.2, C.3, C.4).
/// </summary>
public sealed class DistillerBackgroundServiceTests
{
    private const string UserId = "user1";
    private const string SessionId = "session1";

    private static readonly string _factJson = """
        {"records":[{"ContentType":"Fact","Title":"Python preference","Domain":"Preferences","Key":"Language","Tags":["python"],"Scope":"Global","Importance":0.7,"Value":"User prefers Python."}]}
        """;

    private static DistillerBackgroundService CreateService(
        out ChannelSessionRegistry registry,
        out FakeConversationManagerRegistry convoRegistry,
        out FakeLlmClientAdapter llm,
        out InMemoryMemoryStore store,
        out FakeWatermarkStore watermarks,
        out FakeDeadLetterStore deadLetter,
        out FakeEventBus eventBus,
        string? llmResponse = null,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        });

        var loggerRegistry = NullLogger<ChannelSessionRegistry>.Instance;
        registry = new ChannelSessionRegistry(options, loggerRegistry);
        convoRegistry = new FakeConversationManagerRegistry();
        llm = new FakeLlmClientAdapter(llmResponse ?? _factJson);
        store = new InMemoryMemoryStore();
        watermarks = new FakeWatermarkStore();
        deadLetter = new FakeDeadLetterStore();
        eventBus = new FakeEventBus();

        return new DistillerBackgroundService(
            registry,
            convoRegistry,
            llm,
            new FakeEmbeddingGenerator(),
            store,
            watermarks,
            deadLetter,
            eventBus,
            options,
            timeProvider ?? TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);
    }

    private static IConversationManager MakeConversation(int messageCount = 3)
    {
        var mgr = new InMemoryConversationManager();
        for (int i = 0; i < messageCount; i++)
        {
            mgr.Append(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i}"));
        }

        return mgr;
    }

    // ── C.3 Happy path ─────────────────────────────────────────────────────────

    /// <summary>
    /// Happy path: dequeue a job, read turns, call LLM, upsert records, advance watermark.
    /// </summary>
    [Fact]
    public async Task Distill_DequeueJob_ReadsTurns_CallsLlm_UpsertsRecord_AdvancesWatermark()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out InMemoryMemoryStore store,
            out FakeWatermarkStore watermarks,
            out _,
            out FakeEventBus eventBus);

        IConversationManager convo = MakeConversation(3);
        convoRegistry.Register(SessionId, convo);

        var job = new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 3);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Start service in background.
        Task svcTask = svc.StartAsync(cts.Token);

        // Enqueue job.
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        // Wait for distillation to complete.
        for (int i = 0; i < 50 && !eventBus.Published.Any(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        Assert.Single(store.AllRecords);
        Assert.Equal(3, watermarks.Get(UserId, SessionId));
        Assert.Single(eventBus.Published.OfType<DistillationCompletedEvent>());

        DistillationCompletedEvent evt = eventBus.Published.OfType<DistillationCompletedEvent>().First();
        Assert.Equal(1, evt.RecordsWritten);
        Assert.Equal(3, evt.NewWatermark);
    }

    /// <summary>Verifies that DistillationCompletedEvent is emitted with the correct record count.</summary>
    [Fact]
    public async Task Distill_EmitsDistillationCompletedEventWithCount()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out _,
            out _,
            out _,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(3));
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.GoalCompletion, 3));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        for (int i = 0; i < 50 && !eventBus.Published.Any(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        DistillationCompletedEvent? evt = eventBus.Published.OfType<DistillationCompletedEvent>().FirstOrDefault();
        Assert.NotNull(evt);
        Assert.Equal(1, evt!.RecordsWritten);
    }

    /// <summary>When the turn window is empty (watermark == upToTurnIndex), skips LLM and emits zero count.</summary>
    [Fact]
    public async Task Distill_EmptyTurnRange_SkipsLlmCall_EmitsCompletedWithZero()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeLlmClientAdapter llm,
            out _,
            out FakeWatermarkStore watermarks,
            out _,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(3));

        // Set watermark to same as upToTurnIndex so window is empty.
        await watermarks.AdvanceAsync(UserId, SessionId, 3, TestContext.Current.CancellationToken);

        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 3));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        for (int i = 0; i < 50 && !eventBus.Published.Any(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // LLM must NOT have been called.
        Assert.Equal(0, llm.CallCount);

        // Event must be emitted with zero records written.
        DistillationCompletedEvent? evt = eventBus.Published.OfType<DistillationCompletedEvent>().FirstOrDefault();
        Assert.NotNull(evt);
        Assert.Equal(0, evt!.RecordsWritten);
    }

    // ── C.4 Watermark guard ────────────────────────────────────────────────────

    /// <summary>When the watermark already covers the job's UpToTurnIndex, the job is a no-op.</summary>
    [Fact]
    public async Task Distill_JobUpToIndexAlreadyReached_NoOps_NoLlmCall_NoUpsert()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeLlmClientAdapter llm,
            out InMemoryMemoryStore store,
            out FakeWatermarkStore watermarks,
            out _,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(5));

        // Watermark is already 5 — job with UpToTurnIndex=3 should no-op.
        await watermarks.AdvanceAsync(UserId, SessionId, 5, TestContext.Current.CancellationToken);

        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 3));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        for (int i = 0; i < 50 && !eventBus.Published.Any(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        Assert.Equal(0, llm.CallCount);
        Assert.Empty(store.AllRecords);

        DistillationCompletedEvent? evt = eventBus.Published.OfType<DistillationCompletedEvent>().FirstOrDefault();
        Assert.NotNull(evt);
        Assert.Equal(0, evt!.RecordsWritten);
    }

    /// <summary>When two jobs queue, the second one is a no-op because the first advanced the watermark.</summary>
    [Fact]
    public async Task Distill_TwoQueuedJobs_SecondSkipsBecauseFirstAdvancedWatermark()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out _,
            out _,
            out _,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(5));

        // First job: turns 0–3; second job: turns 0–3 (duplicate).
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 3));

        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 3));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        // Wait for both events.
        for (int i = 0; i < 100 && eventBus.Published.Count(e => e is DistillationCompletedEvent) < 2; i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        IEnumerable<DistillationCompletedEvent> completedEvents =
            eventBus.Published.OfType<DistillationCompletedEvent>();
        Assert.Equal(2, completedEvents.Count());

        // First event should have written records; second should be zero (watermark guard).
        int[] recordCounts = completedEvents.Select(e => e.RecordsWritten).ToArray();
        Assert.Contains(0, recordCounts); // second job was no-op
    }
}
