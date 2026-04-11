using System.Text.Json;

namespace Agency.Llm.Common.Messages;

/// <summary>Identifies the author of an <see cref="AgentMessage"/>.</summary>
public enum MessageRole { System, User, Assistant }

/// <summary>Base type for all Anthropic-shaped content blocks.</summary>
public abstract record ContentBlock;

/// <summary>A plain text content block.</summary>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>
/// A tool invocation request emitted by the assistant.
/// Every <see cref="ToolUseBlock"/> must be paired with a <see cref="ToolResultBlock"/>
/// carrying the same <see cref="Id"/> before the next LLM call.
/// </summary>
public sealed record ToolUseBlock(
    string Id,
    string Name,
    JsonElement Input) : ContentBlock;

/// <summary>
/// Carries the result of a tool invocation back to the LLM.
/// <see cref="ToolUseId"/> must match the paired <see cref="ToolUseBlock.Id"/>.
/// </summary>
public sealed record ToolResultBlock(
    string ToolUseId,
    string Content,
    bool IsError = false) : ContentBlock;

/// <summary>Extended thinking content emitted by Claude 3.7+ models.</summary>
public sealed record ThinkingBlock(string Thinking) : ContentBlock;

/// <summary>A single message in the conversation, carrying one or more content blocks.</summary>
public sealed record AgentMessage(
    MessageRole Role,
    IReadOnlyList<ContentBlock> Content);
