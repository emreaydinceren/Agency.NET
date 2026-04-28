namespace Agency.Mcp.Memory;

/// <summary>
/// Represents the scope of a memory operation, identifying the user and session context.
/// </summary>
public record class MemoryScope (string UserId, string? SessionId)
{
}
