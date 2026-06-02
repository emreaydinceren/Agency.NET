namespace Agency.Harness.Contexts;

/// <summary>
/// A lightweight projection of a memory store record for use in the agent's context.
/// Carries only the fields that <see cref="SystemPromptBuilder"/> needs to render the
/// <c>## Facts</c> and <c>## Memories</c> sections.
/// </summary>
/// <param name="Title">The short human-readable title of the record (≤ 60 chars).</param>
/// <param name="Value">The Markdown body of the record.</param>
/// <param name="UpdatedAt">The UTC timestamp when the record was last updated; used to compute recency.</param>
public sealed record MemoryRecord(string Title, string Value, DateTimeOffset UpdatedAt);
