namespace Agency.Agentic.Contexts;

/// <summary>Caller identity and preferences injected into the system prompt.</summary>
public sealed record UserSpecificContext
{
    /// <summary>Gets the shared empty user context.</summary>
    public static UserSpecificContext Empty { get; } = new();

    /// <summary>Gets the user's display name.</summary>
    public string? Name { get; init; }
}
