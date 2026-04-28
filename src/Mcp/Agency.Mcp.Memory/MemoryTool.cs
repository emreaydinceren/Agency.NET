using Agency.KeyValueStore.Common;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Agency.Mcp.Memory;

/// <summary>
/// MCP server tool class that exposes memory operations (memorize, forget, recall) backed by an <see cref="IKVStore"/>.
/// </summary>
[McpServerToolType, Description(ToolDescription.Text)]
public class MemoryTool(IKVStore kvStore)
{
    private readonly IKVStore _kvStore = kvStore;

    private sealed class DomainMetadata
    {
        public HashSet<string> Keys { get; } = [];

        public HashSet<string> Tags { get; } = [];
    }

    /// <summary>
    /// Stores a piece of information in the memory store under a composite key derived from scope, domain, and key.
    /// </summary>
    /// <param name="record">The memory record to store, including scope, domain, key, value, and optional tags.</param>
    /// <returns>
    /// A confirmation message including the composite storage key, or an error message if validation fails.
    /// </returns>
    [McpServerTool, Description("Memorizes a provided piece of information.")]
    public async Task<string> Memorize(MemoryRecord record)
    {
        if (record.Scope is null)
        {
            return "Error: Scope is required.";
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

        MemoryScope scope = record.Scope;
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
    [McpServerTool, Description("Deletes a memorized piece of information.")]
    public async Task<string> Forget(MemoryScope scope, string domain, string key)
    {
        string storageKey = $"{domain}|{key}";
        bool removed = await this._kvStore.DeleteAsync(scope.UserId, scope.SessionId, storageKey);
        return removed ? $"Removed: {storageKey}" : $"Not found: {storageKey}";
    }

    /// <summary>
    /// Lists the distinct storage keys and tags for all entries in the global (user-wide) session.
    /// </summary>
    /// <param name="userId">The user whose global-session entries to index.</param>
    /// <returns>A JSON object with <c>Keys</c> and <c>Tags</c> string arrays.</returns>
    [McpServerTool, Description("Lists distinct keys and tags stored in the global (user-wide) session for a given user.")]
    public async Task<string> ListGlobalKeys(MemoryScope memoryScope)
    {
        IReadOnlyList<SearchHit> hits = await this._kvStore.GetMetadataAsync(memoryScope.UserId, memoryScope.SessionId);

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
    [McpServerTool, Description("Recalls the memorized piece of information based on filter parameters.")]
    public async Task<string> Recall(MemoryScope scope, string? domain, string? key, string[]? tags)
    {
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

        var query = new Query(scope.UserId, scope.SessionId, queryKey, null, metadataFilter.Count > 0 ? metadataFilter : null, 10, true);
        IReadOnlyList<SearchHit<string>> hits = await this._kvStore.SearchAsync<string>(query);
        return JsonSerializer.Serialize(hits);
    }
}
