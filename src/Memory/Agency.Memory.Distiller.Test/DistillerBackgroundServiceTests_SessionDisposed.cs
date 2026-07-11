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
/// Tests for <see cref="DistillerBackgroundService"/> terminal cleanup behaviour after
/// a <see cref="DistillationTrigger.SessionDisposed"/> job is processed (Spec §A3).
/// </summary>
public sealed class DistillerBackgroundServiceTests_SessionDisposed
{
    private const string UserId = "userX";
    private const string SessionId = "sessionX";

    private static readonly string _factJson = """
        {"records":[{"ContentType":"Fact","Title":"Session end","Domain":"Test","Key":"EndKey","Tags":[],"Scope":"Global","Importance":0.5,"Value":"Session ended."}]}
        """;

    private static DistillerBackgroundService CreateService(
        out ChannelSessionRegistry channelRegistry,
        out FakeConversationManagerRegistry convoRegistry,
        out FakeEventBus eventBus,
        string? llmResponse = null)
    {
        var options = Options.Create(new DistillerOptions
        {
            MaxRetries = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        });

        channelRegistry = new ChannelSessionRegistry(
            options, NullLogger<ChannelSessionRegistry>.Instance);
        convoRegistry = new FakeConversationManagerRegistry();
        var llm = new FakeLlmClientAdapter(llmResponse ?? _factJson);
        var store = new InMemoryMemoryStore();
        var watermarks = new FakeWatermarkStore();
        var deadLetter = new FakeDeadLetterStore();
        eventBus = new FakeEventBus();

        return new DistillerBackgroundService(
            channelRegistry,
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

    private static InMemoryConversationManager MakeConversation(int messageCount = 4)
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
    /// After a <see cref="DistillationTrigger.SessionDisposed"/> job is successfully processed,
    /// the conversation is unregistered from the conversation registry and the channel is removed
    /// from the channel registry.
    /// </summary>
    [Fact]
    public async Task ProcessSessionDisposed_UnregistersConversation_AndRemovesChannel()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry channelRegistry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(4));

        var job = new DistillationJob(UserId, SessionId, DistillationTrigger.SessionDisposed, UpToTurnIndex: 4);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        // Enqueue the SessionDisposed job.
        channelRegistry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        // Wait for the distillation-completed event.
        for (int i = 0; i < 100 && !eventBus.Published.Any(e => e is DistillationCompletedEvent); i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // Conversation should be unregistered.
        Assert.Null(convoRegistry.Get(SessionId));

        // Channel should be removed (GetAll no longer contains sessionId).
        Assert.DoesNotContain(SessionId, channelRegistry.GetAll().Keys);
    }

    /// <summary>
    /// A non-terminal job (Inactivity trigger) does NOT unregister the conversation or remove
    /// the channel — cleanup is exclusive to <see cref="DistillationTrigger.SessionDisposed"/>.
    /// </summary>
    [Fact]
    public async Task ProcessInactivityJob_DoesNotUnregisterConversation_OrRemoveChannel()
    {
        DistillerBackgroundService svc = CreateService(
            out ChannelSessionRegistry channelRegistry,
            out FakeConversationManagerRegistry convoRegistry,
            out FakeEventBus eventBus);

        convoRegistry.Register(SessionId, MakeConversation(4));

        var job = new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 4);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = svc.StartAsync(cts.Token);

        channelRegistry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        for (int i = 0; i < 100 && !eventBus.Published.Any(e => e is DistillationCompletedEvent); i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        // Conversation manager must still be registered.
        Assert.NotNull(convoRegistry.Get(SessionId));

        // Channel must still exist (not removed).
        Assert.Contains(SessionId, channelRegistry.GetAll().Keys);
    }
}
