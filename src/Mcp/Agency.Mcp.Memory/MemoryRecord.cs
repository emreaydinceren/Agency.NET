using System.ComponentModel;

namespace Agency.Mcp.Memory;

/// <summary>
/// Represents a record to be stored in memory, including its scope, domain, key, value, and optional tags.
/// </summary>
public record class MemoryRecord
{
    /// <summary>Gets or sets the scope (user and session) under which this record is stored.</summary>
    [Description("Ownership boundary for the record. Set UserId to the literal placeholder \"{userId}\"; omit SessionId for user-wide (global) memory.")]
    public MemoryScope? Scope { set; get; }

    /// <summary>Gets or sets the key that identifies this record within its domain.</summary>
    [Description("Stable, human-readable identifier for this item within its domain (e.g. \"FavouriteColour\", \"HomeAddress\").")]
    public string? Key { set; get; }

    /// <summary>Gets or sets the domain grouping this record belongs to.</summary>
    [Description("High-level category the record belongs to (e.g. \"Personal\", \"Work\", \"Health\").")]
    public string? Domain { set; get; }

    /// <summary>Gets or sets the value to store.</summary>
    [Description("The information to remember, stored as text.")]
    public string? Value { set; get; }

    /// <summary>Gets or sets optional tags to associate with this record for filtered recall.</summary>
    [Description("Optional labels for cross-domain retrieval (e.g. [\"taxes\",\"family\"]).")]
    public string[]? Tags { set; get; }
}
