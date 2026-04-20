namespace Agency.Agentic.Contexts;

/// <summary>Operating-environment facts injected into the system prompt.</summary>
public sealed record EnvironmentalContext
{
    public EnvironmentalContext()
    {
    }

    /// <summary>Gets the shared empty environment context.</summary>
    public static EnvironmentalContext Empty { get; } = new();

    /// <summary>Gets the operating system name/version.</summary>
    public string? OperatingSystem { get; init; }
}
