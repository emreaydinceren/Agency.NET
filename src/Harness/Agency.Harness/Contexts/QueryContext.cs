namespace Agency.Harness.Contexts;

/// <summary>The user's intent for this agent session.</summary>
public sealed record QueryContext
{
    /// <summary>Gets the user's prompt that seeds the conversation.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets the resolved project instruction files to inject as a separate message before the prompt.</summary>
    public string? InstructionsBlock { get; init; }
}
