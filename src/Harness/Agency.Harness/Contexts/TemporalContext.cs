namespace Agency.Harness.Contexts;

/// <summary>Current date/time injected into the system prompt as a grounding cue.</summary>
public sealed record TemporalContext
{
    /// <summary>Initializes a new, empty <see cref="TemporalContext"/>.</summary>
    public TemporalContext()
    {
    }

    /// <summary>Gets the shared empty temporal context.</summary>
    public static TemporalContext Empty { get; } = new();

    /// <summary>Gets the UTC timestamp at session start.</summary>
    public DateTimeOffset? CurrentDateUtc { get; init; }
}
