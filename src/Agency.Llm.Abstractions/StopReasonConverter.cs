namespace Agency.Llm.Abstractions;

/// <summary>
/// Converts provider finish-reason values into the shared stop-reason enum.
/// </summary>
public static class FinishReasonConverter
{
    /// <summary>
    /// Converts a provider finish-reason string to <see cref="StopReason"/>.
    /// </summary>
    public static StopReason ToStopReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason))
        {
            return StopReason.Unknown;
        }

        var normalized = finishReason
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "stop" => StopReason.Stop,
            "endturn" => StopReason.EndTurn,
            "maxtokens" => StopReason.MaxTokens,
            "length" => StopReason.Length,
            "stopsequence" => StopReason.StopSequence,
            "tooluse" => StopReason.ToolUse,
            "toolcalls" => StopReason.ToolCalls,
            "functioncall" => StopReason.FunctionCall,
            "contentfilter" => StopReason.ContentFilter,
            _ => StopReason.Unknown,
        };
    }

    /// <summary>
    /// Converts a provider finish-reason string to a nullable <see cref="StopReason"/>.
    /// </summary>
    public static StopReason? ToNullableStopReason(string? finishReason)
    {
        return string.IsNullOrWhiteSpace(finishReason) ? null : ToStopReason(finishReason);
    }
}
