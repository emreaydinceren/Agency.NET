using System.ComponentModel;

namespace Agency.Mcp.Memory;

/// <summary>
/// Represents the scope of a memory operation, identifying the user and session context.
/// </summary>
public record class MemoryScope (
    [property: Description("Owner of the memory. Always pass the literal placeholder \"{userId}\"; the host substitutes the real user id. Never invent, guess, or ask the user for this value.")]
    string UserId,
    [property: Description("Optional task/conversation partition under the user. Omit or pass null for user-wide (global) memory, e.g. when recalling everything known about the user.")]
    string? SessionId)
{
}
