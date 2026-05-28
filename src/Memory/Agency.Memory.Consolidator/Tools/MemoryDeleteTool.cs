using System.Text.Json;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Consolidator.Tools;

/// <summary>
/// Consolidator tool that hard-deletes a single record by its surrogate id
/// (Spec §6.3 — <c>Memory_Delete</c>).
/// </summary>
/// <remarks>
/// Deletion is irreversible. Bumps <c>LastWrittenAt</c> so the retrieval gate
/// (Spec §8.1) invalidates on the next query.
/// </remarks>
internal sealed class MemoryDeleteTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "recordId": {
                    "type": "string",
                    "description": "The UUID of the record to hard-delete."
                }
            },
            "required": ["recordId"]
        }
        """).RootElement;

    private readonly IMemoryStore _store;
    private readonly string _userId;

    /// <summary>
    /// Initialises a new <see cref="MemoryDeleteTool"/>.
    /// </summary>
    /// <param name="store">The memory store for delete operations.</param>
    /// <param name="userId">The owning user — constrains the delete to that user's records.</param>
    internal MemoryDeleteTool(IMemoryStore store, string userId)
    {
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._userId = userId ?? throw new ArgumentNullException(nameof(userId));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => new(
        Name: "Memory_Delete",
        Description: "Hard-deletes a single record by its ID. Irreversible.",
        InputSchema: _inputSchema);

    /// <inheritdoc/>
    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("recordId", out JsonElement idEl)
            || idEl.ValueKind != JsonValueKind.String
            || idEl.GetString() is not { } recordId)
        {
            return new ToolResult("recordId is required.", IsError: true);
        }

        try
        {
            bool deleted = await this._store.DeleteByIdAsync(recordId, this._userId, ct).ConfigureAwait(false);

            if (!deleted)
            {
                return new ToolResult($"Record {recordId} not found.", IsError: true);
            }

            return new ToolResult($"Deleted record {recordId}.");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Delete failed: {ex.Message}", IsError: true);
        }
    }
}
