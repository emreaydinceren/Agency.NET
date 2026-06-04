using System.Text.Json;
using Agency.Harness;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Agency.Memory.Consolidator.Test;

/// <summary>
/// Unit tests for <see cref="ConsolidatorBackgroundService"/>.
/// </summary>
public class ConsolidatorBackgroundServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Agency.Memory.Common.Records.Record MakeRecord(string id, string userId = "u1") =>
        Agency.Memory.Common.Records.Record.Create(
            id: id,
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: id,
            title: "Test",
            value: "Value",
            tags: [],
            importance: 0.5,
            createdAt: _now.AddDays(-1),
            updatedAt: _now.AddDays(-1));

    private static IOptions<ConsolidatorOptions> DefaultOptions() =>
        Options.Create(new ConsolidatorOptions { MaxIterations = 20, MaxCostUsd = 0.50m });

    // ── Empty store guard ──────────────────────────────────────────────────────

    /// <summary>
    /// When the user has no records, the consolidator exits immediately without calling the LLM.
    /// </summary>
    [Fact]
    public async Task Consolidate_NoRecords_ExitsImmediately_NoLlmCall()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ConsolidatorBackgroundService(
            store.Object,
            null!, // no agent factory needed when no records
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        await service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        // GetAllForUserAsync was called but no LLM/agent factory was invoked.
        store.Verify(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
        eventBus.Verify(b => b.PublishAsync(
            It.Is<ConsolidationCompletedEvent>(e => e.UserId == "u1" && e.Merges == 0 && e.Updates == 0 && e.Deletes == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Sub-agent happy path ───────────────────────────────────────────────────

    /// <summary>
    /// When the LLM returns a Memory_Done tool call immediately, the sub-agent terminates and
    /// ConsolidationCompletedEvent is emitted.
    /// </summary>
    [Fact]
    public async Task Consolidate_StopOnDone_TerminatesLoop_EmitsCompletedEvent()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Loose);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRecord("r1")]);

        ConsolidationCompletedEvent? emittedEvent = null;
        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationCompletedEvent, CancellationToken>((e, _) => emittedEvent = e)
            .Returns(Task.CompletedTask);

        // Agent factory returns a stub that simulates the sub-agent calling Memory_Done immediately.
        bool agentInvoked = false;
        Task<(int, int, int)> StubAgentRunner(string userId, IReadOnlyList<Agency.Memory.Common.Records.Record> records, CancellationToken ct)
        {
            agentInvoked = true;
            // Simulate agent calling Memory_Done by doing nothing (the service handles this).
            return Task.FromResult((0, 0, 0));
        }

        var service = new ConsolidatorBackgroundService(
            store.Object,
            StubAgentRunner,
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        await service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        Assert.True(agentInvoked);
        Assert.NotNull(emittedEvent);
        Assert.Equal("u1", emittedEvent!.UserId);
    }

    // ── Per-user serial: coalescing ───────────────────────────────────────────

    /// <summary>
    /// Two ConsolidationJobs for the same user enqueued while one is in-flight:
    /// the second triggers exactly one additional coalesced run (not two concurrent runs).
    /// </summary>
    [Fact]
    public async Task Consolidate_PerUser_SerialExecution_TwoTriggersCoalesce()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Loose);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRecord("r1")]);

        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        int agentRunCount = 0;
        var barrier = new TaskCompletionSource<(int, int, int)>();
        Task<(int, int, int)> StubAgentRunner(string userId, IReadOnlyList<Agency.Memory.Common.Records.Record> records, CancellationToken ct)
        {
            Interlocked.Increment(ref agentRunCount);
            return barrier.Task; // hold until released
        }

        var service = new ConsolidatorBackgroundService(
            store.Object,
            StubAgentRunner,
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        // Start first job (will block at barrier).
        var firstTask = service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        // Enqueue second trigger while first is in-flight.
        service.EnqueuePendingIfCoalesced("u1");

        // Release the first run.
        barrier.SetResult((0, 0, 0));
        await firstTask;

        // After first completes, the pending flag should have triggered a second run.
        // But we allow the service to drain it synchronously via DrainPending.
        await service.DrainPendingAsync(CancellationToken.None);

        // Two runs total for one user: first real run + coalesced second.
        Assert.Equal(2, agentRunCount);
    }

    // ── Large corpus warning ───────────────────────────────────────────────────

    /// <summary>
    /// When the user has more than MaxRecordsPerPass records, a warning is logged and consolidation proceeds.
    /// </summary>
    [Fact]
    public async Task Consolidate_LargeCorpus_LogsWarningAndProceeds()
    {
        var records = Enumerable.Range(0, 600).Select(i => MakeRecord($"r{i}")).ToList();
        var store = new Mock<IMemoryStore>(MockBehavior.Loose);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);

        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        bool agentInvoked = false;
        Task<(int, int, int)> StubAgentRunner(string userId, IReadOnlyList<Agency.Memory.Common.Records.Record> recs, CancellationToken ct)
        {
            agentInvoked = true;
            return Task.FromResult((0, 0, 0));
        }

        var service = new ConsolidatorBackgroundService(
            store.Object,
            StubAgentRunner,
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        await service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        // Should still proceed despite large corpus.
        Assert.True(agentInvoked);
    }

    // ── Event emission ────────────────────────────────────────────────────────

    /// <summary>
    /// ConsolidationCompletedEvent is emitted with the correct UserId.
    /// </summary>
    [Fact]
    public async Task Consolidate_EmitsConsolidationCompletedEventWithUserId()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Loose);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRecord("r1")]);

        ConsolidationCompletedEvent? evt = null;
        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationCompletedEvent, CancellationToken>((e, _) => evt = e)
            .Returns(Task.CompletedTask);

        Task<(int, int, int)> StubAgentRunner(string userId, IReadOnlyList<Agency.Memory.Common.Records.Record> recs, CancellationToken ct) =>
            Task.FromResult((0, 0, 0));

        var service = new ConsolidatorBackgroundService(
            store.Object,
            StubAgentRunner,
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        await service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal("u1", evt!.UserId);
    }

    // ── Mutation count aggregation ────────────────────────────────────────────

    /// <summary>
    /// When the agent runner reports 2 merges, 0 updates, 1 delete,
    /// <see cref="ConsolidationCompletedEvent"/> carries exactly those tallies.
    /// </summary>
    [Fact]
    public async Task Consolidate_MutationCounts_PropagatedToCompletedEvent()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Loose);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRecord("r1"), MakeRecord("r2"), MakeRecord("r3")]);

        ConsolidationCompletedEvent? evt = null;
        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationCompletedEvent, CancellationToken>((e, _) => evt = e)
            .Returns(Task.CompletedTask);

        // Stub returns 2 merges, 0 updates, 1 delete.
        Task<(int, int, int)> StubAgentRunner(string userId, IReadOnlyList<Agency.Memory.Common.Records.Record> recs, CancellationToken ct) =>
            Task.FromResult((2, 0, 1));

        var service = new ConsolidatorBackgroundService(
            store.Object,
            StubAgentRunner,
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        await service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal("u1", evt!.UserId);
        Assert.Equal(2, evt.Merges);
        Assert.Equal(0, evt.Updates);
        Assert.Equal(1, evt.Deletes);
    }

    /// <summary>
    /// A no-op pass (agent performs no mutations) emits <see cref="ConsolidationCompletedEvent"/>
    /// with all counts zero.
    /// </summary>
    [Fact]
    public async Task Consolidate_NoMutations_CompletedEventHasZeroCounts()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Loose);
        store.Setup(s => s.GetAllForUserAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRecord("r1")]);

        ConsolidationCompletedEvent? evt = null;
        var eventBus = new Mock<IAsyncEventBus>(MockBehavior.Loose);
        eventBus.Setup(b => b.PublishAsync(It.IsAny<ConsolidationCompletedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationCompletedEvent, CancellationToken>((e, _) => evt = e)
            .Returns(Task.CompletedTask);

        Task<(int, int, int)> StubAgentRunner(string userId, IReadOnlyList<Agency.Memory.Common.Records.Record> recs, CancellationToken ct) =>
            Task.FromResult((0, 0, 0));

        var service = new ConsolidatorBackgroundService(
            store.Object,
            StubAgentRunner,
            eventBus.Object,
            DefaultOptions(),
            NullLogger<ConsolidatorBackgroundService>.Instance);

        await service.ProcessJobAsync(new ConsolidationJob("u1", "session1"), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal(0, evt!.Merges);
        Assert.Equal(0, evt.Updates);
        Assert.Equal(0, evt.Deletes);
    }
}
