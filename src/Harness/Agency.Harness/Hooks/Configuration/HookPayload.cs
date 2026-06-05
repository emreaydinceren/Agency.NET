using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agency.Harness.Hooks.Configuration;

internal sealed record HookPayload
{
    public string? SessionId { get; init; }
    public string? HookEventName { get; init; }
    public string? Cwd { get; init; }
    public int IterationCount { get; init; }
    public double TotalCostUsd { get; init; }
    public string? ToolName { get; init; }
    public JsonElement? ToolInput { get; init; }
    public ToolResponsePayload? ToolResponse { get; init; }
    public string? Prompt { get; init; }
    public string? Message { get; init; }

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed record ToolResponsePayload(string Content, bool IsError);