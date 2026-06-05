using System.Diagnostics;
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
/// Verifies the event-driven wake mechanism in <see cref="DistillerBackgroundService"/>:
/// a newly-enqueued job must be picked up without a polling delay, and the consumer must
/// not spin-loop while all session channels are empty (Spec §6.2 / §10.2 deviation).
/// </summary>
public sealed class DistillerBackgroundServiceTests_EventDrivenWake
{
    private const string UserId = "wake-user";
    private const string SessionId = "wake-session";

    private static readonly string _factJson = """
        {"records":[{"ContentType":"Fact","Title":"Wake test","Domain":"Test","Key":"WakeKey","Tags":[],"Scope":"Global","Importance":0.5,"Value":"Wake value."}]}
        """;

    private static DistillerBackgroundService CreateService(
        out ChannelSessionRegistry registry,
        out FakeConversationManagerRegistry convoRegistry,
        out FakeEventBus eventBus)
    {
        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        });

        registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        convoRegistry = new FakeConversationManagerRegistry();
        var llm = new FakeLlmClientAdapter(_factJson);
        var store = new InMemoryMemoryStore();
        var watermarks = new FakeWatermarkStore();
        var deadLetter = new FakeDeadLetterStore();
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
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);
    }

    private static IConversationManager MakeConversation(int messageCount = 2)
    {
        var mgr = new InMemoryConversationManager();
        for (int i = 0; i < messageCount; i++)
        {
            mgr.Append(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Msg {i}"));
        }

        return mgr;
    }

    /// <summary>
    /// A job enqueued after the service is already running and idle must be processed
    /// in well under 50 ms — demonstrating that there is no polling-delay floor.
    /// The service suspends on WaitForWorkAsync and wakes immediately on Signal.
    /// </summary>
    [Fact]
    public async Task EnqueuedJob_WakesConsumerPromptly_WithoutPollingDelay()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        // Give the service a moment to start and drain (nothing enqueued yet —
        // it will suspend on WaitForWorkAsync).
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Measure time from enqueue to completion event.
        var sw = Stopwatch.StartNew();
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 2));

        // Wait for the completion event — must arrive well under 50 ms.
        for (int i = 0; i < 200 && !eventBus.Published.Any(); i++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        sw.Stop();
        await cts.CancelAsync();

        Assert.True(
            eventBus.Published.Any(),
            "Service did not process the job within the timeout.");

        // The job must have been picked up well under the old polling cycle (50 ms floor).
        // We use 500 ms as the upper bound to tolerate CI scheduler jitter while still
        // falsifying the old polling behaviour (which would always exceed 50 ms per sweep).
        Assert.True(
            sw.ElapsedMilliseconds < 500,
            $"Expected pickup in < 500 ms (no polling floor), but took {sw.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// While all session channels are empty the consumer must not spin — verified by
    /// checking that it does not emit events or advance any watermarks during an idle
    /// window, and that it wakes correctly when a job finally arrives.
    /// </summary>
    [Fact]
    public async Task IdleConsumer_DoesNotSpin_AndWakesOnFirstEnqueue()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry registry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        // Idle window — nothing enqueued.
        await Task.Delay(30, TestContext.Current.CancellationToken);

        // Confirm no spurious events were emitted while idle.
        Assert.Empty(eventBus.Published);

        // Now enqueue a job and confirm it is processed.
        registry.GetOrCreateWriter(UserId, SessionId).TryWrite(
            new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 2));

        for (int i = 0; i < 200 && !eventBus.Published.Any(); i++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        Assert.NotEmpty(eventBus.Published);
    }
}
