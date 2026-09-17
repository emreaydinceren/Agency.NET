using Microsoft.Extensions.AI;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// A minimal <see cref="IChatClient"/> test double that serves one predetermined
/// <see cref="ChatResponse"/> per call, in FIFO order. <c>Agency.Harness.Agent</c> drives every
/// turn through <see cref="GetStreamingResponseAsync"/> (D-7), so the enqueued response is
/// decomposed into the two-update shape <c>ChatResponseExtensions.ToChatResponseAsync</c> reassembles.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    /// <summary>Gets the number of times a response was requested.</summary>
    public int CallCount { get; private set; }

    /// <summary>Gets the messages received on each call, in order — used to prove session isolation.</summary>
    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

    /// <summary>Enqueues a response returned on the next call.</summary>
    public void EnqueueResponse(ChatResponse response) => this._responses.Enqueue(response);

    /// <inheritdoc/>
    public ChatClientMetadata Metadata { get; } = new("FakeChatClient", null, null);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        this.CallCount++;
        this.ReceivedMessages.Add(messages.ToList());
        return Task.FromResult(this.Dequeue());
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        this.CallCount++;
        this.ReceivedMessages.Add(messages.ToList());
        ChatResponse response = this.Dequeue();
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }

        await Task.CompletedTask;
    }

    private ChatResponse Dequeue()
    {
        if (this._responses.Count == 0)
        {
            throw new InvalidOperationException($"FakeChatClient has no more queued responses (call #{this.CallCount}).");
        }

        return this._responses.Dequeue();
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? key = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }
}
