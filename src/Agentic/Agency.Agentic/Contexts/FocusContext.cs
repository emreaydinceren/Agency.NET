namespace Agency.Agentic.Contexts;

/// <summary>
/// Narrows the retrieval query toward a particular task domain, as set by <c>SetFocusTool</c>.
/// Focus terms are appended to the query text before embedding, biasing retrieval without
/// forcing exact-match filtering (Spec §6.4, §6.7.1).
/// </summary>
public sealed record FocusContext
{
    /// <summary>Gets the shared empty focus context (no active focus).</summary>
    public static FocusContext Empty { get; } = new();

    /// <summary>Gets the short title describing the current focus area (e.g., "Auth Debugging").</summary>
    public string? Title { get; init; }

    /// <summary>Gets the semantic domain for the current focus (e.g., "Debugging").</summary>
    public string? Domain { get; init; }

    /// <summary>Gets the tags associated with the current focus (e.g., ["ssl", "dns"]).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
