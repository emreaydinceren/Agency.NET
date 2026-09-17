using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// An <see cref="IChatClient"/> whose <see cref="GetStreamingResponseAsync"/> hangs until
/// cancelled. Mirrors <c>Agency.Harness.Test.TurnTimeoutTests.HangingChatClient</c>, which is not
/// visible from this assembly.
/// </summary>
internal sealed class HangingChatClient : IChatClient
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once <see cref="GetStreamingResponseAsync"/> has started running.</summary>
    public Task Entered => this._entered.Task;

    /// <inheritdoc/>
    public ChatClientMetadata Metadata { get; } = new("HangingChatClient", null, null);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("HangingChatClient only supports the streaming path.");

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        this._entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? key = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }
}
