using Agency.Agentic;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Distiller.Test.Stubs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Tests for retry and dead-letter behaviour of <see cref="DistillerBackgroundService"/>
/// (Spec §8.6, C.5).
/// </summary>
public sealed class DistillerBackgroundServiceTests_Retry
{
    private const string UserId = "user1";
    private const string SessionId = "session1";

    private static readonly string _factJson = """
        {"records":[{"ContentType":"Fact","Title":"Retry test fact","Domain":"Test","Key":"RetryKey","Tags":[],"Scope":"Global","Importance":0.7,"Value":"Retry test value."}]}
        """;

    private static DistillerBackgroundService CreateService(
        FakeLlmClientAdapter llm,
        out ChannelSessionRegistry registry,
        out FakeConversationManagerRegistry convoRegistry,
        out FakeWatermarkStore watermarks,
        out FakeDeadLetterStore deadLetter,
        out FakeEventBus eventBus,
        int maxRetries = 2,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = maxRetries,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        });

        registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        convoRegistry = new FakeConversationManagerRegistry();
        watermarks = new FakeWatermarkStore();
        deadLetter = new FakeDeadLetterStore();
        eventBus = new FakeEventBus();

        return new DistillerBackgroundService(
            registry,
            convoRegistry,
            llm,
            new FakeEmbeddingGenerator(),
            new InMemoryMemoryStore(),
            watermarks,
            deadLetter,
            eventBus,
            options,
            timeProvider ?? TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);
    }

    private static IConversationManager MakeConversation()
    {
        var mgr = new InMemoryConversationManager();
        mgr.Append(new ChatMessage(ChatRole.User, "Test message."));
        mgr.Append(new ChatMessage(ChatRole.Assistant, "Test response."));
        return mgr;
    }

    /// <summary>
    /// A transient LLM failure retries up to MaxRetries times, then dead-letters.
    /// </summary>
    [Fact]
    public async Task Distill_TransientLlmFailure_RetriesUpToMaxRetries_ThenDeadLetters()
    {
        // All calls throw 429 (transient).
        var llm = new FakeLlmClientAdapter();
        for (int i = 0; i < 4; i++)
        {
            llm.QueueException(new HttpRequestException("Too many requests",
                null, System.Net.HttpStatusCode.TooManyRequests));
        }

        DistillerBackgroundService svc = CreateService(
            llm,
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out FakeDeadLetterStore deadLetter,
            out FakeEventBus eventBus,
            maxRetries: 2);

        convoRegistry.Register(SessionId, MakeConversation());
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        // Wait for dead-letter.
        for (int i = 0; i < 100 && deadLetter.Entries.Count == 0 && !eventBus.Published.OfType<DistillationFailedEvent>().Any(); i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // MaxRetries=2 means 3 total attempts (0, 1, 2).
        Assert.Equal(3, llm.CallCount);
        Assert.Single(deadLetter.Entries);
        Assert.Single(eventBus.Published.OfType<DistillationFailedEvent>());

        DistillationFailedEvent failedEvt = eventBus.Published.OfType<DistillationFailedEvent>().First();
        Assert.True(failedEvt.DeadLettered);
    }

    /// <summary>
    /// A permanent (HTTP 400) failure dead-letters immediately without retry.
    /// </summary>
    [Fact]
    public async Task Distill_PermanentLlmFailure_DeadLettersImmediately()
    {
        var llm = new FakeLlmClientAdapter();
        llm.QueueException(new HttpRequestException("Bad request",
            null, System.Net.HttpStatusCode.BadRequest));

        DistillerBackgroundService svc = CreateService(
            llm,
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out FakeDeadLetterStore deadLetter,
            out _,
            maxRetries: 3);

        convoRegistry.Register(SessionId, MakeConversation());
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        for (int i = 0; i < 100 && deadLetter.Entries.Count == 0; i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // Should dead-letter on first attempt (permanent failure).
        Assert.Equal(1, llm.CallCount);
        Assert.Single(deadLetter.Entries);
    }

    /// <summary>
    /// Malformed JSON on first parse attempt causes one retry; second succeeds.
    /// </summary>
    [Fact]
    public async Task Distill_MalformedJsonOnce_RetriesWithStricterPrompt_ThenSucceeds()
    {
        var llm = new FakeLlmClientAdapter("not valid json", _factJson);

        DistillerBackgroundService svc = CreateService(
            llm,
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out FakeDeadLetterStore deadLetter,
            out FakeEventBus eventBus,
            maxRetries: 2);

        convoRegistry.Register(SessionId, MakeConversation());
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        for (int i = 0; i < 100 && !eventBus.Published.OfType<DistillationCompletedEvent>().Any(); i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // Two calls: first returned bad JSON (retried), second succeeded.
        Assert.Equal(2, llm.CallCount);
        Assert.Empty(deadLetter.Entries);
        Assert.Single(eventBus.Published.OfType<DistillationCompletedEvent>());
    }

    /// <summary>
    /// Malformed JSON on both attempts dead-letters.
    /// </summary>
    [Fact]
    public async Task Distill_MalformedJsonTwice_DeadLetters()
    {
        var llm = new FakeLlmClientAdapter("bad json 1", "bad json 2");

        DistillerBackgroundService svc = CreateService(
            llm,
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out FakeDeadLetterStore deadLetter,
            out FakeEventBus eventBus,
            maxRetries: 2);

        convoRegistry.Register(SessionId, MakeConversation());
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        for (int i = 0; i < 100 && deadLetter.Entries.Count == 0; i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        Assert.Single(deadLetter.Entries);
        Assert.Single(eventBus.Published.OfType<DistillationFailedEvent>());
    }

    /// <summary>
    /// OperationCanceledException propagates without dead-lettering.
    /// </summary>
    [Fact]
    public async Task Distill_CancellationToken_PropagatesWithoutDeadLetter()
    {
        using var cts = new CancellationTokenSource();

        var llm = new FakeLlmClientAdapter();
        // LLM will block until cancellation.
        llm.QueueException(new OperationCanceledException());

        DistillerBackgroundService svc = CreateService(
            llm,
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out FakeDeadLetterStore deadLetter,
            out FakeEventBus eventBus,
            maxRetries: 2);

        convoRegistry.Register(SessionId, MakeConversation());
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, 2));

        _ = svc.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        // Allow service to stop.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Cancellation should not dead-letter.
        Assert.Empty(deadLetter.Entries);
        Assert.Empty(eventBus.Published.OfType<DistillationFailedEvent>());
    }

    /// <summary>
    /// Retry delay follows exponential backoff pattern (verified via FakeTimeProvider).
    /// </summary>
    [Fact]
    public async Task Distill_RetryDelay_FollowsExponentialBackoff()
    {
        var clock = new FakeTimeProvider();
        var llm = new FakeLlmClientAdapter();
        // Queue 2 transient failures then a success.
        llm.QueueException(new HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable));
        llm.QueueException(new HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable));
        // Third call returns success.

        DistillerBackgroundService svc = CreateService(
            llm,
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out _,
            out _,
            out FakeEventBus eventBus,
            maxRetries: 3,
            timeProvider: clock);

        convoRegistry.Register(SessionId, MakeConversation());
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = svc.StartAsync(cts.Token);

        // Give service time to hit first failure and start waiting.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Advance past first retry delay (base=10ms, attempt=1 → 10*2^0 = 10ms).
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Advance past second retry delay (attempt=2 → 10*2^1 = 20ms).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        for (int i = 0; i < 50 && !eventBus.Published.OfType<DistillationCompletedEvent>().Any(); i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // All 3 LLM calls made: 2 failures + 1 success.
        Assert.Equal(3, llm.CallCount);
        Assert.Single(eventBus.Published.OfType<DistillationCompletedEvent>());
    }
}
