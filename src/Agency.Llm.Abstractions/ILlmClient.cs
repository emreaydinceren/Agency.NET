namespace Agency.Llm.Abstractions;

/// <summary>
/// Defines a common contract for Llm provider clients.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a completion request and returns the generated response.
    /// </summary>
    Task<LlmResponse> SendAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a completion response as it is generated.
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a completed LLM response.
/// </summary>
public sealed record LlmResponse(
    /// <summary>
    /// Gets the generated response text.
    /// </summary>
    string Message,
    /// <summary>
    /// Gets the final stop reason.
    /// </summary>
    StopReason FinishReason,
    /// <summary>
    /// Gets the token usage statistics.
    /// </summary>
    LlmTokenUsage Usage);

/// <summary>
/// Represents token usage for a request.
/// </summary>
public sealed record LlmTokenUsage(
    /// <summary>
    /// Gets the number of input tokens.
    /// </summary>
    long InputTokens,
    /// <summary>
    /// Gets the number of output tokens.
    /// </summary>
    long OutputTokens)
{
    /// <summary>
    /// Gets the total token count.
    /// </summary>
    public long TotalTokens => this.InputTokens + this.OutputTokens;
}

/// <summary>
/// Represents a single chunk in a streaming LLM response.
/// Text delta chunks have <see cref="Text"/> set and <see cref="Usage"/>/<see cref="StopReason"/> null.
/// The final terminal chunk has <see cref="Text"/> null and <see cref="Usage"/>/<see cref="StopReason"/> set.
/// </summary>
public sealed record LlmStreamChunk(
    /// <summary>
    /// Gets the streamed text chunk, if any.
    /// </summary>
    string? Text,
    /// <summary>
    /// Gets the terminal stop reason, if any.
    /// </summary>
    StopReason? StopReason,
    /// <summary>
    /// Gets the usage recorded for the chunk, if any.
    /// </summary>
    LlmTokenUsage? Usage);

/// <summary>
/// Enumerates supported stop reasons for LLM responses.
/// </summary>
public enum StopReason
{
    Unknown = 0,
    Stop,
    EndTurn,
    MaxTokens,
    Length,
    StopSequence,
    ToolUse,
    ToolCalls,
    FunctionCall,
    ContentFilter,
}
