using System.Text.Json;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Consolidator.Tools;

/// <summary>
/// Consolidator tool that atomically deletes the listed records and inserts a new combined record
/// (Spec §6.3 / §8.4 — <c>Memory_Merge</c>).
/// </summary>
/// <remarks>
/// Atomicity is guaranteed by <see cref="IMemoryStore.MergeAsync"/>: the implementation
/// executes DELETE + INSERT in a single PostgreSQL transaction.
/// </remarks>
internal sealed class MemoryMergeTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "recordIds": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "The IDs of the records to delete and merge together."
                },
                "newRecord": {
                    "type": "object",
                    "description": "The new combined record that replaces the listed records.",
                    "properties": {
                        "contentType": { "type": "string", "enum": ["Fact", "Memory"] },
                        "domain": { "type": "string" },
                        "key": { "type": "string" },
                        "title": { "type": "string" },
                        "value": { "type": "string" },
                        "tags": { "type": "array", "items": { "type": "string" } },
                        "importance": { "type": "number", "minimum": 0, "maximum": 1 },
                        "scope": { "type": "string", "enum": ["Global", "Session"] }
                    },
                    "required": ["contentType", "domain", "key", "title", "value", "tags", "importance"]
                }
            },
            "required": ["recordIds", "newRecord"]
        }
        """).RootElement;

    private readonly IMemoryStore _store;
    private readonly string _userId;

    /// <summary>
    /// Initialises a new <see cref="MemoryMergeTool"/>.
    /// </summary>
    /// <param name="store">The memory store for atomic merge operations.</param>
    /// <param name="userId">The owning user — ensures the merge only touches that user's records.</param>
    internal MemoryMergeTool(IMemoryStore store, string userId)
    {
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._userId = userId ?? throw new ArgumentNullException(nameof(userId));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => new(
        Name: "Memory_Merge",
        Description: "Atomically deletes the listed records and inserts a new combined record that merges their content.",
        InputSchema: _inputSchema);

    /// <inheritdoc/>
    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("recordIds", out JsonElement idsEl)
            || idsEl.ValueKind != JsonValueKind.Array)
        {
            return new ToolResult("recordIds is required and must be an array.", IsError: true);
        }

        if (!input.TryGetProperty("newRecord", out JsonElement newRecordEl)
            || newRecordEl.ValueKind != JsonValueKind.Object)
        {
            return new ToolResult("newRecord is required and must be an object.", IsError: true);
        }

        var ids = new List<string>();
        foreach (JsonElement idEl in idsEl.EnumerateArray())
        {
            if (idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { } id)
            {
                ids.Add(id);
            }
        }

        Record? newRecord = ParseRecord(newRecordEl, this._userId);
        if (newRecord is null)
        {
            return new ToolResult("newRecord is missing required fields (contentType, domain, key, title, value).", IsError: true);
        }

        try
        {
            Record inserted = await this._store.MergeAsync(ids, newRecord, ct).ConfigureAwait(false);
            return new ToolResult($"Merged {ids.Count} record(s) into new record {inserted.Id}.");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Merge failed: {ex.Message}", IsError: true);
        }
    }

    private static Record? ParseRecord(JsonElement el, string userId)
    {
        if (!el.TryGetProperty("contentType", out JsonElement ctEl)
            || !el.TryGetProperty("domain", out JsonElement domainEl)
            || !el.TryGetProperty("key", out JsonElement keyEl)
            || !el.TryGetProperty("title", out JsonElement titleEl)
            || !el.TryGetProperty("value", out JsonElement valueEl))
        {
            return null;
        }

        string? ctString = ctEl.GetString();
        ContentType contentType = ctString switch
        {
            "Memory" => ContentType.Memory,
            _ => ContentType.Fact,
        };

        string domain = domainEl.GetString() ?? string.Empty;
        string key = keyEl.GetString() ?? string.Empty;
        string title = titleEl.GetString() ?? string.Empty;
        string value = valueEl.GetString() ?? string.Empty;

        var tags = new List<string>();
        if (el.TryGetProperty("tags", out JsonElement tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tagEl in tagsEl.EnumerateArray())
            {
                if (tagEl.GetString() is { } tag)
                {
                    tags.Add(tag);
                }
            }
        }

        double importance = 0.5;
        if (el.TryGetProperty("importance", out JsonElement impEl)
            && impEl.ValueKind == JsonValueKind.Number)
        {
            importance = Math.Clamp(impEl.GetDouble(), 0.0, 1.0);
        }

        var now = DateTimeOffset.UtcNow;
        return Record.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: contentType,
            domain: domain,
            key: key,
            title: title,
            value: value,
            tags: tags,
            importance: importance,
            createdAt: now,
            updatedAt: now);
    }
}
