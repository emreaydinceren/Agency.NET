namespace Agency.Mcp.Memory;

/// <summary>
/// Represents a record to be stored in memory, including its scope, domain, key, value, and optional tags.
/// </summary>
public record class MemoryRecord
{
    /// <summary>Gets or sets the scope (user and session) under which this record is stored.</summary>
    public MemoryScope? Scope { set; get; }

    /// <summary>Gets or sets the key that identifies this record within its domain.</summary>
    public string? Key { set; get; }

    /// <summary>Gets or sets the domain grouping this record belongs to.</summary>
    public string? Domain { set; get; }

    /// <summary>Gets or sets the value to store.</summary>
    public string? Value { set; get; }

    /// <summary>Gets or sets optional tags to associate with this record for filtered recall.</summary>
    public string[]? Tags { set; get; }
}
