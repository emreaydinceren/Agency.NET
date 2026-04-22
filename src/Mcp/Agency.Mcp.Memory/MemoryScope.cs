namespace Agency.Mcp.Memory;

/// <summary>
/// Represents the scope of a memory operation, identifying the user and session context.
/// </summary>
public record class MemoryScope
{
    /// <summary>Gets or sets the user identifier. May be empty to represent a global scope.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the session identifier. May be empty to represent a user-wide scope.</summary>
    public string SessionId { get; set; } = string.Empty;
}
