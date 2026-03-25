namespace Agency.Llm.Abstractions;

/// <summary>
/// Defines a common contract for Llm provider clients.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> SendAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default);
}

public sealed record LlmResponse(
    string Message,
    StopReason FinishReason,
    LlmTokenUsage Usage);

public sealed record LlmTokenUsage(
    long InputTokens,
    long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Represents a single chunk in a streaming LLM response.
/// Text delta chunks have <see cref="Text"/> set and <see cref="Usage"/>/<see cref="StopReason"/> null.
/// The final terminal chunk has <see cref="Text"/> null and <see cref="Usage"/>/<see cref="StopReason"/> set.
/// </summary>
public sealed record LlmStreamChunk(
    string? Text,
    StopReason? StopReason,
    LlmTokenUsage? Usage);

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
