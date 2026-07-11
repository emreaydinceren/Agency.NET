using Agency.KeyValueStore.Common;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Agency.Mcp.Memory;

/// <summary>
/// MCP server tool class that exposes memory operations (memorize, forget, recall) backed by an <see cref="IKVStore"/>.
/// </summary>
[McpServerToolType, Description(ToolDescription.Text)]
public sealed class MemoryTool(IKVStore kvStore)
{
    private readonly IKVStore _kvStore = kvStore;

    private sealed class DomainMetadata
    {
        public HashSet<string> Keys { get; } = [];

        public HashSet<string> Tags { get; } = [];
    }

    /// <summary>
    /// Validates that a scope is present and carries a non-empty user id. Returns an error message to
    /// surface to the caller, or <see langword="null"/> when the scope is valid. Guards against a missing
    /// or partial scope payload (e.g. <c>{}</c>) reaching the store, where a null user id throws.
    /// </summary>
    private static string? ValidateScope(MemoryScope? scope)
    {
        if (scope is null)
        {
            return "Error: scope is required and must be a nested object. Retry with: {\"scope\": {\"userId\": \"{userId}\"}}.";
        }

        if (string.IsNullOrWhiteSpace(scope.UserId))
        {
            return "Error: scope.userId is required. Pass the literal placeholder. Retry with: {\"scope\": {\"userId\": \"{userId}\"}}.";
        }

        return null;
    }

    /// <summary>
    /// Stores a piece of information in the memory store under a composite key derived from scope, domain, and key.
    /// </summary>
    /// <param name="record">The memory record to store, including scope, domain, key, value, and optional tags.</param>
    /// <returns>
    /// A confirmation message including the composite storage key, or an error message if validation fails.
    /// </returns>
    [McpServerTool, Description("Saves a piece of information to long-term memory so it can be recalled in later turns or future sessions. Use this whenever the user shares a durable fact about themselves or explicitly asks you to remember something (preferences, names, addresses, policies). The single required argument is a nested OBJECT named record containing: Scope (itself an object whose UserId is the literal placeholder \"{userId}\" — the host fills in the real id, never invent it), a Domain (high-level category, e.g. \"Personal\"), a Key (stable identifier within that domain, e.g. \"FavouriteColour\"), and the Value to store; optionally add Tags for cross-domain retrieval. A valid call looks exactly like this: {\"record\": {\"scope\": {\"userId\": \"{userId}\"}, \"domain\": \"Personal\", \"key\": \"FavouriteColour\", \"value\": \"blue\"}}. Returns the composite storage key on success.")]
    public async Task<string> Memorize(MemoryRecord? record = null)
    {
        if (record is null)
        {
            return "Error: record is required. Retry with: {\"record\": {\"scope\": {\"userId\": \"{userId}\"}, \"domain\": \"Personal\", \"key\": \"FavouriteColour\", \"value\": \"blue\"}}.";
        }

        if (ValidateScope(record.Scope) is string scopeError)
        {
            return scopeError;
        }

        if (string.IsNullOrWhiteSpace(record.Domain))
        {
            return "Error: Domain is required.";
        }

        if (string.IsNullOrWhiteSpace(record.Key))
        {
            return "Error: Key is required.";
        }

        if (string.IsNullOrWhiteSpace(record.Value))
        {
            return "Error: Value is required.";
        }

        MemoryScope scope = record.Scope!;
        string storageKey = $"{record.Domain}|{record.Key}";

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = record.Domain,
            ["key"] = record.Key
        };

        if (record.Tags is { Length: > 0 })
        {
            metadata["tags"] = record.Tags;
        }

        await this._kvStore.UpsertAsync(scope.UserId, scope.SessionId, storageKey, record.Value, metadata);
        return $"Memorized: {storageKey}";
    }

    /// <summary>
    /// Deletes the memorized entry identified by the composite key formed from scope, domain, and key.
    /// </summary>
    /// <param name="scope">The scope (user and session) identifying the owner of the entry.</param>
    /// <param name="domain">The domain grouping the entry belongs to.</param>
    /// <param name="key">The specific key of the entry to delete.</param>
    /// <returns>A message indicating whether the entry was removed or not found.</returns>
    [McpServerTool, Description("Permanently deletes a single memorized entry identified by its Domain and Key within the given Scope. Use this when the user asks you to forget a specific fact. scope is a nested OBJECT, not a string, with UserId set to the literal placeholder \"{userId}\". A valid call looks like this: {\"scope\": {\"userId\": \"{userId}\"}, \"domain\": \"Personal\", \"key\": \"FavouriteColour\"}. Returns whether the entry was removed or was not found.")]
    public async Task<string> Forget(
        MemoryScope? scope = null,
        [Description("High-level category of the entry to delete (the same Domain it was memorized under).")] string? domain = null,
        [Description("Specific item identifier within the domain to delete (the same Key it was memorized under).")] string? key = null)
    {
        if (ValidateScope(scope) is string scopeError)
        {
            return scopeError;
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return "Error: domain is required.";
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return "Error: key is required.";
        }

        string storageKey = $"{domain}|{key}";
        // CA1062: ValidateScope above already guards the null case (returning a friendly error
        // string instead of throwing, matching this MCP tool's error-as-text contract); the
        // analyzer can't trace that indirection through a helper method.
#pragma warning disable CA1062
        bool removed = await this._kvStore.DeleteAsync(scope!.UserId, scope.SessionId, storageKey);
#pragma warning restore CA1062
        return removed ? $"Removed: {storageKey}" : $"Not found: {storageKey}";
    }

    /// <summary>
    /// Lists the distinct storage keys and tags for all entries in the global (user-wide) session.
    /// </summary>
    /// <param name="scope">The scope (user and optional session) whose global-session entries to index.</param>
    /// <returns>A JSON object with <c>Keys</c> and <c>Tags</c> string arrays.</returns>
    [McpServerTool, Description("Lists the distinct Domains, Keys, and Tags already stored for the user across the global (user-wide) session. Call this first for broad discovery — to see what is already known about the user before deciding what to Recall. IMPORTANT: scope is a nested OBJECT, not a string, and is the only required argument — do NOT call with empty arguments {} or you will get an error. The only valid call looks exactly like this: {\"scope\": {\"userId\": \"{userId}\"}}. Returns JSON grouped by domain.")]
    public async Task<string> ListGlobalKeys(MemoryScope? scope = null)
    {
        if (ValidateScope(scope) is string scopeError)
        {
            return scopeError;
        }

        // CA1062: ValidateScope above already guards the null case (returning a friendly error
        // string instead of throwing, matching this MCP tool's error-as-text contract); the
        // analyzer can't trace that indirection through a helper method.
#pragma warning disable CA1062
        IReadOnlyList<SearchHit> hits = await this._kvStore.GetMetadataAsync(scope!.UserId, scope.SessionId);
#pragma warning restore CA1062

        Dictionary<string, DomainMetadata> items = new();

        foreach (var hit in hits)
        {
            if (hit.Metadata?.TryGetValue("domain", out object? domain) == true && string.IsNullOrWhiteSpace(domain?.ToString()) == false)
            {
                if (items.TryGetValue(domain.ToString()!, out DomainMetadata? data) == false)
                {
                    data = new DomainMetadata();
                    items[domain.ToString()!] = data;
                }

                if (hit.Metadata?.TryGetValue("tags", out object? tags) == true)
                {
                    IEnumerable<string> tagValues = tags switch
                    {
                        string[] tagsArray => tagsArray,
                        IEnumerable<object> objectTags => objectTags.Select(t => t?.ToString()).OfType<string>(),
                        JsonElement { ValueKind: JsonValueKind.Array } tagsJson => tagsJson.EnumerateArray().Select(t => t.GetString()).OfType<string>(),
                        _ => []
                    };

                    foreach (var tag in tagValues)
                    {
                        if (string.IsNullOrWhiteSpace(tag) == false)
                        {
                            data.Tags.Add(tag);
                        }
                    }
                }

                if (hit.Metadata?.TryGetValue("key", out object? key) == true)
                {
                    string? keyValue = key switch
                    {
                        string s => s,
                        JsonElement { ValueKind: JsonValueKind.String } keyJson => keyJson.GetString(),
                        _ => key?.ToString()
                    };

                    if (string.IsNullOrWhiteSpace(keyValue) == false)
                    {
                        data.Keys.Add(keyValue);
                    }
                }
            }
        }

        return JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// Searches the memory store for entries matching the provided scope, domain, key, and/or tags.
    /// </summary>
    /// <param name="scope">The scope (user and session) to filter by. Always applied.</param>
    /// <param name="domain">
    /// The domain to filter by. If both domain and key are provided, an exact composite key match is used.
    /// </param>
    /// <param name="key">
    /// The key to filter by. If both domain and key are provided, an exact composite key match is used.
    /// </param>
    /// <param name="tags">Optional tags to filter by. Entries must contain all specified tags.</param>
    /// <returns>A JSON-serialized array of <see cref="SearchHit{TValue}"/> results.</returns>
    [McpServerTool, Description("Retrieves previously memorized information about the user from long-term memory. Use this to answer questions about what you know about the user or to look up a stored fact. Always pass a Scope with UserId set to the literal placeholder \"{userId}\" (the host substitutes the real id — never invent, guess, or ask for it). IMPORTANT: scope is a nested OBJECT, not a string, and is the only required argument — do NOT call with empty arguments {} or you will get an error. The simplest valid call recalls everything known about the user and looks exactly like this: {\"scope\": {\"userId\": \"{userId}\"}}. Every other parameter is an OPTIONAL filter: to recall EVERYTHING known about the user, pass only the Scope and leave domain, key, and tags empty. Provide both Domain and Key for an exact lookup, Domain alone to list one category, or Tags to filter by label. For broad discovery, call ListGlobalKeys first. Returns a JSON array of matching entries (an empty array when nothing matches).")]
    public async Task<string> Recall(
        MemoryScope? scope = null,
        [Description("Optional. High-level category to filter by (e.g. \"Personal\", \"Work\"). Combine with Key for an exact lookup; omit to search across all domains.")] string? domain = null,
        [Description("Optional. Specific item identifier within the domain. Only applied as an exact lookup when Domain is also provided.")] string? key = null,
        [Description("Optional. Restrict results to entries containing all of these tags.")] string[]? tags = null)
    {
        if (ValidateScope(scope) is string scopeError)
        {
            return scopeError;
        }

        var metadataFilter = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(domain))
        {
            metadataFilter["domain"] = domain;
        }

        if (tags is { Length: > 0 })
        {
            metadataFilter["tags"] = tags;
        }

        string? queryKey = null;
        if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(key))
        {
            queryKey = $"{domain}|{key}";
        }

        // CA1062: ValidateScope above already guards the null case (returning a friendly error
        // string instead of throwing, matching this MCP tool's error-as-text contract); the
        // analyzer can't trace that indirection through a helper method.
#pragma warning disable CA1062
        var query = new Query(scope!.UserId, scope.SessionId, queryKey, null, metadataFilter.Count > 0 ? metadataFilter : null, 10, true);
#pragma warning restore CA1062
        IReadOnlyList<SearchHit<string>> hits = await this._kvStore.SearchAsync<string>(query);
        return JsonSerializer.Serialize(hits);
    }
}
