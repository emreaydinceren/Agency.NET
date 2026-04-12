namespace Agency.Agentic.Test.Fakes;

/// <summary>
/// A hand-rolled test double for <see cref="ILlmClient"/> that serves predetermined
/// <see cref="AgentLlmResponse"/> objects in FIFO order for <c>SendAgentAsync</c> calls.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<AgentLlmResponse> _agentResponses = new();

    /// <summary>Gets the number of times <c>SendAgentAsync</c> was called.</summary>
    public int SendAgentCallCount { get; private set; }

    /// <summary>Gets the system prompts received on each call, in order.</summary>
    public List<string> ReceivedSystemPrompts { get; } = [];

    /// <summary>Gets the full messages list received on each call, in order.</summary>
    public List<IReadOnlyList<AgentMessage>> ReceivedMessages { get; } = [];

    /// <summary>Enqueues a response that will be returned on the next <c>SendAgentAsync</c> call.</summary>
    public void EnqueueAgentResponse(AgentLlmResponse response) => _agentResponses.Enqueue(response);

    public Task<AgentLlmResponse> SendAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SendAgentCallCount++;
        ReceivedSystemPrompts.Add(systemPrompt);
        // Snapshot the list at call-time — the agent appends messages after each call,
        // so storing the live reference would cause ReceivedMessages entries to grow retroactively.
        ReceivedMessages.Add(messages.ToList());

        if (_agentResponses.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeLlmClient has no more queued responses (call #{SendAgentCallCount}).");
        }

        return Task.FromResult(_agentResponses.Dequeue());
    }

    /// <inheritdoc/>
    public Task<LlmResponse> SendAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Use SendAgentAsync for agent loop tests.");

    /// <inheritdoc/>
    public IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Use StreamAgentAsync for agent loop tests.");

    /// <inheritdoc/>
    public IAsyncEnumerable<AgentStreamChunk> StreamAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
        => throw new NotImplementedException("Use SendAgentAsync for agent loop tests.");

    /// <inheritdoc/>
    public Task<IReadOnlyList<Model>> GetModels(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Use SendAgentAsync for agent loop tests.");
}
