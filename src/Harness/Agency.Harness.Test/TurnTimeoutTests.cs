using Agency.Harness.Contexts;
using Microsoft.Extensions.Time.Testing;

namespace Agency.Harness.Test;

/// <summary>
/// Tests that a turn timeout is distinguishable from a user-initiated cancellation: today both
/// collapse into a bare <see cref="OperationCanceledException"/>, so a supervisor cannot tell
/// "the model hung" from "the human hit Stop".
/// </summary>
public sealed class TurnTimeoutTests
{
    /// <summary>An <see cref="IChatClient"/> whose <c>GetResponseAsync</c> hangs until cancelled.</summary>
    private sealed class HangingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once <c>GetResponseAsync</c> has started running.</summary>
        public Task Entered => _entered.Task;

        public ChatClientMetadata Metadata { get; } = new("HangingChatClient", null, null);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }

    private static Context MakeContext() => Agent.CreateContext("Hello");

    /// <summary>
    /// When the LLM hangs past <see cref="AgentOptions.TurnTimeoutSeconds"/>, the turn must fail
    /// with a <see cref="TimeoutException"/> — not a bare <see cref="OperationCanceledException"/>
    /// that a supervisor cannot tell apart from a user cancel.
    /// </summary>
    [Fact]
    public async Task TurnExceedsTimeout_ThrowsTimeoutException()
    {
        var clock = new FakeTimeProvider();
        var client = new HangingChatClient();
        var agent = new Agent(client, "model-a", timeProvider: clock);
        var ctx = MakeContext();
        var options = new AgentOptions { TurnTimeoutSeconds = 30 };

        var runTask = Task.Run(async () =>
        {
            await foreach (var _ in agent.ChatAsync("hi", ctx, options, TestContext.Current.CancellationToken))
            {
            }
        });

        await client.Entered;
        clock.Advance(TimeSpan.FromSeconds(31));

        await Assert.ThrowsAsync<TimeoutException>(() => runTask);
    }

    /// <summary>
    /// When the caller's own token is cancelled (not the timeout), the turn must fail with an
    /// ordinary <see cref="OperationCanceledException"/> — proving the two are separable even
    /// when a timeout is configured but has not yet fired.
    /// </summary>
    [Fact]
    public async Task CallerCancels_ThrowsOperationCanceledException_NotTimeoutException()
    {
        var clock = new FakeTimeProvider();
        var client = new HangingChatClient();
        var agent = new Agent(client, "model-a", timeProvider: clock);
        var ctx = MakeContext();
        var options = new AgentOptions { TurnTimeoutSeconds = 30 };

        using var cts = new CancellationTokenSource();

        var runTask = Task.Run(async () =>
        {
            await foreach (var _ in agent.ChatAsync("hi", ctx, options, cts.Token))
            {
            }
        });

        await client.Entered;
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.IsNotType<TimeoutException>(ex);
    }
}
