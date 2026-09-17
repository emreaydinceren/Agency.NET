namespace Agency.Harness.Test.Fakes;

/// <summary>
/// A hand-rolled test double for <see cref="IChatClient"/> that serves predetermined
/// <see cref="ChatResponse"/> objects in FIFO order for <c>GetResponseAsync</c> and
/// <c>GetStreamingResponseAsync</c> calls.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<object> _responses = new();

    /// <summary>Gets the number of times <c>GetResponseAsync</c> was called.</summary>
    public int GetResponseCallCount { get; private set; }

    /// <summary>Gets the instructions (system prompts) received on each call, in order.</summary>
    public List<string> ReceivedSystemPrompts { get; } = [];

    /// <summary>Gets the full messages list received on each call, in order.</summary>
    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

    /// <summary>Enqueues a response returned on the next call.</summary>
    public void EnqueueResponse(ChatResponse response) => _responses.Enqueue(response);

    /// <summary>Enqueues an exception to be thrown on the next call.</summary>
    public void EnqueueException(Exception exception) => _responses.Enqueue(exception);

    /// <summary>
    /// Enqueues hand-written <see cref="ChatResponseUpdate"/>s to be yielded verbatim by the next
    /// <c>GetStreamingResponseAsync</c> call, instead of decomposing a whole <see cref="ChatResponse"/>
    /// into its two-update shape. Used by tests that need genuine multi-delta streaming (e.g.
    /// interleaved text/reasoning fragments). The corresponding <c>GetResponseAsync</c> call
    /// aggregates the same updates via <c>ChatResponseExtensions.ToChatResponseAsync</c> so both
    /// call shapes stay consistent for a single enqueued item.
    /// </summary>
    public void EnqueueUpdates(params ChatResponseUpdate[] updates) => _responses.Enqueue(updates);

    /// <inheritdoc/>
    public ChatClientMetadata Metadata { get; } = new("FakeChatClient", null, null);

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetResponseCallCount++;
        RecordCall(messages, options);

        object next = Dequeue();
        if (next is ChatResponseUpdate[] updates)
        {
            return await ToAsyncEnumerable(updates).ToChatResponseAsync(cancellationToken);
        }

        return (ChatResponse)next;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetResponseCallCount++;
        RecordCall(messages, options);

        object next = Dequeue();
        IAsyncEnumerable<ChatResponseUpdate> updates = next switch
        {
            ChatResponseUpdate[] scripted => ToAsyncEnumerable(scripted),
            ChatResponse response => ToAsyncEnumerable(response.ToChatResponseUpdates()),
            _ => throw new InvalidOperationException($"Unexpected queued item type: {next.GetType()}."),
        };

        await foreach (ChatResponseUpdate update in updates.WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    private void RecordCall(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.Instructions is not null)
        {
            ReceivedSystemPrompts.Add(options.Instructions);
        }

        // Snapshot the list so each ReceivedMessages entry is stable across subsequent appends.
        ReceivedMessages.Add(messages.ToList());
    }

    private object Dequeue()
    {
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeChatClient has no more queued responses (call #{GetResponseCallCount}).");
        }

        object next = _responses.Dequeue();
        if (next is Exception ex)
        {
            throw ex;
        }

        return next;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(
        IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (ChatResponseUpdate update in updates)
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? key = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }
}
