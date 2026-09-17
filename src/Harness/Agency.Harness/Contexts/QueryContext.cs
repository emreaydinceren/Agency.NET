namespace Agency.Harness.Contexts;

/// <summary>The user's intent for this agent session.</summary>
public sealed record QueryContext
{
    /// <summary>Gets the user's prompt that seeds the conversation.</summary>
    public required string Prompt { get; init; }

    /// <summary>Gets the resolved project instruction files to inject as a separate message before the prompt.</summary>
    public string? InstructionsBlock { get; init; }

    /// <summary>
    /// Gets the Persona-supplied identity text that replaces only the opening identity line of the
    /// system prompt (spec §6.9 D-3); <see langword="null"/> keeps the default line. Everything
    /// else the system prompt builder assembles — the ReAct instruction, knowledge, and grounding
    /// sections — is unaffected.
    /// </summary>
    public string? IdentityPrompt { get; init; }
}
