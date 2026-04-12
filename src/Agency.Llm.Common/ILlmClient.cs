using Agency.Llm.Common.Messages;
using Agency.Llm.Common.Tools;

namespace Agency.Llm.Common;

/// <summary>
/// Defines a common contract for Llm provider clients.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a structured agent request carrying typed messages and tool definitions, and returns the fully assembled
    /// assistant response. Provider implementations should override this to support the agent loop.
    /// </summary>
    Task<AgentLlmResponse> SendAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            $"{this.GetType().Name} does not implement {nameof(this.SendAgentAsync)}.");

    /// <summary>
    /// Streams a structured agent response, yielding text deltas for live display and complete
    /// <see cref="AgentStreamChunk.ToolUse"/> blocks once each tool call is fully received. The final chunk has
    /// <see cref="AgentStreamChunk.Text"/> and <see cref="AgentStreamChunk.ToolUse"/> both null, with
    /// <see cref="AgentStreamChunk.StopReason"/> and <see cref="AgentStreamChunk.Usage"/> set. Provider implementations
    /// should override this to support the streaming agent loop.
    /// </summary>
    IAsyncEnumerable<AgentStreamChunk> StreamAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            $"{this.GetType().Name} does not implement {nameof(this.StreamAgentAsync)}.");

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

    /// <summary>
    /// Asynchronously retrieves a read-only list of available models.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of models.</returns>
    Task<IReadOnlyList<Model>> GetModels(CancellationToken cancellationToken = default);
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
/// Represents a single chunk in a streaming LLM response. Text delta chunks have <see cref="Text"/> set and
/// <see cref="Usage"/> /<see cref="StopReason"/> null. The final terminal chunk has <see cref="Text"/> null and
/// <see cref="Usage"/> /<see cref="StopReason"/> set.
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
/// Represents a single chunk in a streaming agent response. Text delta chunks have <see cref="Text"/> set; all other
/// fields are null. Tool-use chunks have <see cref="ToolUse"/> set; all other fields are null. The terminal chunk has
/// <see cref="StopReason"/> and <see cref="Usage"/> set; text and tool-use are null.
/// </summary>
public sealed record AgentStreamChunk(
    /// <summary>
    /// Gets the streamed text delta, if any.
    /// </summary>
    string? Text,
    /// <summary>
    /// Gets the completed tool-use block, if any.
    /// </summary>
    Messages.ToolUseBlock? ToolUse,
    /// <summary>
    /// Gets the terminal stop reason, if any.
    /// </summary>
    StopReason? StopReason,
    /// <summary>
    /// Gets the token usage for the full response, if any (terminal chunk only).
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
    Refusal,    // Added for Claude 3.5+ and OpenAI O1
    PauseTurn   // Added for 2026 Server-Side Tool Loops
}


public sealed record Model(string Id, string Name);