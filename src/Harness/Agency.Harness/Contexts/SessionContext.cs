namespace Agency.Harness.Contexts;

/// <summary>Stable per-session identity used to tag and rank memory records (Spec P3).</summary>
public sealed record SessionContext
{
    /// <summary>Gets the shared empty session context.</summary>
    public static SessionContext Empty { get; } = new();

    /// <summary>Gets the stable session identifier; <see langword="null"/> until the agent loop assigns one.</summary>
    public string? Id { get; init; }
}