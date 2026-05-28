namespace Agency.Agentic.Contexts;

/// <summary>Caller identity and preferences injected into the system prompt.</summary>
public sealed record UserSpecificContext
{
    /// <summary>Gets the shared empty user context.</summary>
    public static UserSpecificContext Empty { get; } = new();

    /// <summary>
    /// Gets the stable user identifier used to partition memory records.
    /// Required by the retrieval engine and distillation pipeline (Spec §6.4).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>Gets the user's display name.</summary>
    public string? Name { get; init; }
}
