using Agency.Harness;
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
/// Tests for <see cref="DistillerBackgroundService"/> graceful shutdown drain (Spec §10.1).
/// Verifies that queued jobs are processed during the drain window, and that any jobs still
/// queued after the timeout are dead-lettered.
/// </summary>
public sealed class DistillerBackgroundServiceTests_ShutdownDrain
{
    private const string UserId = "drain-user";
    private const string SessionId = "drain-session";

    private static readonly string _factJson = """
        {"records":[{"ContentType":"Fact","Title":"Drain fact","Domain":"Test","Key":"DrainKey","Tags":[],"Scope":"Global","Importance":0.5,"Value":"Drained."}]}
        """;

    private static DistillerBackgroundService CreateService(
        out ChannelSessionRegistry registry,
        out FakeConversationManagerRegistry convoRegistry,
        out FakeDeadLetterStore deadLetter,
        out FakeEventBus eventBus,
        TimeSpan? drainTimeout = null,
        string? llmResponse = null)
    {
        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            ShutdownDrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5),
        });

        registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        convoRegistry = new FakeConversationManagerRegistry();
        deadLetter = new FakeDeadLetterStore();
        eventBus = new FakeEventBus();

        return new DistillerBackgroundService(
            registry,
            convoRegistry,
            new FakeLlmClientAdapter(llmResponse ?? _factJson),
            new FakeEmbeddingGenerator(),
            new InMemoryMemoryStore(),
            new FakeWatermarkStore(),
            deadLetter,
            eventBus,
            options,
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);
    }

    private static InMemoryConversationManager MakeConversation(int messageCount = 3)
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

    /// <summary>
    /// Jobs queued before shutdown are processed during the drain window when the
    /// LLM responds instantly and the drain timeout is generous.
    /// </summary>
    [Fact]
    public async Task DrainAsync_WithinTimeout_ProcessesQueuedJobs()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeDeadLetterStore deadLetter,
            out FakeEventBus eventBus,
            drainTimeout: TimeSpan.FromSeconds(5));

        convoRegistry.Register(SessionId, MakeConversation(3));

        // Pre-queue two jobs before drain starts.
        registry.GetOrCreateWriter(UserId, SessionId)
            .TryWrite(new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 3));

        await svc.DrainAsync(TimeSpan.FromSeconds(5));

        // All jobs were processed — nothing dead-lettered.
        Assert.Empty(deadLetter.Entries);

        // At least one DistillationCompletedEvent was published.
        Assert.NotEmpty(eventBus.Published.OfType<DistillationCompletedEvent>());
    }

    /// <summary>
    /// When the drain timeout is zero, the drain pass has no time to process jobs.
    /// All remaining queued jobs must be dead-lettered.
    /// </summary>
    [Fact]
    public async Task DrainAsync_TimeoutExpired_DeadLettersRemainingJobs()
    {
        // Use a slow LLM stub that never returns within the zero-length drain window.
        var slowLlm = new SlowLlmClientAdapter(delay: TimeSpan.FromSeconds(10), response: _factJson);

        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            ShutdownDrainTimeout = TimeSpan.Zero,
        });

        var registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        var convoRegistry = new FakeConversationManagerRegistry();
        var deadLetter = new FakeDeadLetterStore();
        var eventBus = new FakeEventBus();

        var svc = new DistillerBackgroundService(
            registry,
            convoRegistry,
            slowLlm,
            new FakeEmbeddingGenerator(),
            new InMemoryMemoryStore(),
            new FakeWatermarkStore(),
            deadLetter,
            eventBus,
            options,
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

        convoRegistry.Register(SessionId, MakeConversation(3));

        // Queue two jobs.
        registry.GetOrCreateWriter(UserId, SessionId)
            .TryWrite(new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 1));

        registry.GetOrCreateWriter(UserId, SessionId)
            .TryWrite(new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 2));

        // Drain with a zero timeout — the slow LLM means nothing drains.
        await svc.DrainAsync(TimeSpan.Zero);

        // Both jobs must appear in the dead-letter store.
        Assert.Equal(2, deadLetter.Entries.Count);
        Assert.All(deadLetter.Entries, e =>
        {
            Assert.Equal(UserId, e.UserId);
            Assert.Equal(SessionId, e.SessionId);
            Assert.Equal("distillation", e.JobKind);
        });
    }

    /// <summary>
    /// When some jobs finish within the drain window and others remain after the timeout,
    /// only the unprocessed remainder is dead-lettered.
    /// </summary>
    [Fact]
    public async Task DrainAsync_PartialDrain_DeadLettersOnlyRemainder()
    {
        // First call succeeds instantly; second call blocks past the short timeout.
        var partialLlm = new PartialSlowLlmClientAdapter(
            fastResponse: _factJson,
            slowDelay: TimeSpan.FromSeconds(10));

        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        var convoRegistry = new FakeConversationManagerRegistry();
        var deadLetter = new FakeDeadLetterStore();
        var eventBus = new FakeEventBus();

        var svc = new DistillerBackgroundService(
            registry,
            convoRegistry,
            partialLlm,
            new FakeEmbeddingGenerator(),
            new InMemoryMemoryStore(),
            new FakeWatermarkStore(),
            deadLetter,
            eventBus,
            options,
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

        convoRegistry.Register(SessionId, MakeConversation(4));

        // Job 1: watermark 0 → 2 (fast).
        registry.GetOrCreateWriter(UserId, SessionId)
            .TryWrite(new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 2));

        // Job 2: watermark 2 → 4 (slow — will timeout).
        registry.GetOrCreateWriter(UserId, SessionId)
            .TryWrite(new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 4));

        // Short drain window: first job finishes, second times out.
        await svc.DrainAsync(TimeSpan.FromMilliseconds(300));

        // Exactly one job was completed successfully.
        Assert.Single(eventBus.Published.OfType<DistillationCompletedEvent>());

        // The unprocessed job must have been dead-lettered.
        Assert.Single(deadLetter.Entries);
        Assert.Equal("distillation", deadLetter.Entries[0].JobKind);
    }

    // ── Local test-only LLM stubs ──────────────────────────────────────────────

    /// <summary>LLM stub that always delays for <see cref="_delay"/> before returning.</summary>
    private sealed class SlowLlmClientAdapter : ILlmClientAdapter
    {
        private readonly TimeSpan _delay;
        private readonly string _response;

        internal SlowLlmClientAdapter(TimeSpan delay, string response)
        {
            this._delay = delay;
            this._response = response;
        }

        /// <inheritdoc/>
        public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
        {
            await Task.Delay(this._delay, ct).ConfigureAwait(false);
            return this._response;
        }
    }

    /// <summary>
    /// LLM stub that returns the first call instantly, then delays all subsequent calls.
    /// </summary>
    private sealed class PartialSlowLlmClientAdapter : ILlmClientAdapter
    {
        private readonly string _fastResponse;
        private readonly TimeSpan _slowDelay;
        private int _callCount;

        internal PartialSlowLlmClientAdapter(string fastResponse, TimeSpan slowDelay)
        {
            this._fastResponse = fastResponse;
            this._slowDelay = slowDelay;
        }

        /// <inheritdoc/>
        public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
        {
            int call = Interlocked.Increment(ref this._callCount);

            if (call == 1)
            {
                return this._fastResponse;
            }

            await Task.Delay(this._slowDelay, ct).ConfigureAwait(false);
            return this._fastResponse;
        }
    }
}
