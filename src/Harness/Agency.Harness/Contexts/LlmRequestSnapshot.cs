using System.Text.Json;

namespace Agency.Harness.Contexts;

/// <summary>
/// A serializable record of the request handed to the chat client on one loop iteration.
/// Captured immediately before the call, so it reflects everything <c>OnPreIteration</c> hooks
/// wrote onto the blackboard - notably retrieved facts and memories, which a pre-flight
/// projection of the next turn cannot see.
/// </summary>
/// <remarks>
/// Stored on <see cref="Context.LastLlmRequest"/> in serialized form rather than by reference:
/// the conversation's message list is live and keeps growing, and <c>ChatMessage</c> is mutable,
/// so only a serialized copy is a genuine record of what was submitted. Round-trips through
/// <see cref="AIJsonUtilities.DefaultOptions"/>, which carries the polymorphic
/// <see cref="AIContent"/> converters.
/// </remarks>
internal sealed record LlmRequestSnapshot
{
    /// <summary>Gets the payload schema version, so other hosts can read older captures.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the UTC time at which the request was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Gets the agent loop iteration this request belongs to.</summary>
    public required int Iteration { get; init; }

    /// <summary>Gets the model identifier the request was sent to.</summary>
    public required string ModelId { get; init; }

    /// <summary>Gets the client-type display name of the agent that sent the request (e.g. "Claude").</summary>
    public required string ClientType { get; init; }

    /// <summary>Gets the output-token cap sent with the request, if any.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Gets the system prompt sent as <c>ChatOptions.Instructions</c>.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Gets the conversation messages as they stood when the request was submitted.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Gets the tool definitions advertised to the model on this request.</summary>
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
}

/// <summary>
/// Serializes and deserializes <see cref="LlmRequestSnapshot"/> as UTF-8 JSON.
/// </summary>
/// <remarks>
/// UTF-8 bytes rather than a <see cref="string"/>: roughly half the memory of the equivalent
/// UTF-16 payload, and directly writable to a file or socket by a non-console host.
/// </remarks>
internal static class LlmRequestSnapshotCodec
{
    /// <summary>Serializes <paramref name="snapshot"/> to UTF-8 JSON.</summary>
    internal static byte[] Serialize(LlmRequestSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, AIJsonUtilities.DefaultOptions);

    /// <summary>Deserializes a snapshot previously produced by <see cref="Serialize"/>.</summary>
    internal static LlmRequestSnapshot? Deserialize(byte[] utf8Json) =>
        JsonSerializer.Deserialize<LlmRequestSnapshot>(utf8Json, AIJsonUtilities.DefaultOptions);
}
