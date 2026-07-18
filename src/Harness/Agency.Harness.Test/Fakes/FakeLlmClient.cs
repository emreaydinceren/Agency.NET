namespace Agency.Harness.Test.Fakes;

/// <summary>
/// A hand-rolled test double for <see cref="IChatClient"/> that serves predetermined
/// <see cref="ChatResponse"/> objects in FIFO order for <c>GetResponseAsync</c> calls.
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

    /// <summary>Enqueues a response returned on the next <c>GetResponseAsync</c> call.</summary>
    public void EnqueueResponse(ChatResponse response) => _responses.Enqueue(response);

    /// <summary>Enqueues an exception to be thrown on the next <c>GetResponseAsync</c> call.</summary>
    public void EnqueueException(Exception exception) => _responses.Enqueue(exception);

    /// <inheritdoc/>
    public ChatClientMetadata Metadata { get; } = new("FakeChatClient", null, null);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetResponseCallCount++;

        if (options?.Instructions is not null)
        {
            ReceivedSystemPrompts.Add(options.Instructions);
        }

        // Snapshot the list so each ReceivedMessages entry is stable across subsequent appends.
        ReceivedMessages.Add(messages.ToList());

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

        return Task.FromResult((ChatResponse)next);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Use GetResponseAsync for agent loop tests.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? key = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }
}
