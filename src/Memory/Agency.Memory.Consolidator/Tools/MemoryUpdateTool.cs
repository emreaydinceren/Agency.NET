using System.Text.Json;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Consolidator.Tools;

/// <summary>
/// Consolidator tool that updates the Value and/or Importance of an existing record
/// (Spec §6.3 — <c>Memory_Update</c>).
/// </summary>
/// <remarks>
/// Only non-null parameters are applied; a null <c>newValue</c> leaves the existing value
/// unchanged, and a null <c>newImportance</c> leaves importance unchanged.
/// <c>UpdatedAt</c> is always refreshed and <c>LastWrittenAt</c> is bumped.
/// </remarks>
internal sealed class MemoryUpdateTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "recordId": {
                    "type": "string",
                    "description": "The UUID of the record to update."
                },
                "newValue": {
                    "type": "string",
                    "description": "The new Markdown value, or omit to leave unchanged."
                },
                "newImportance": {
                    "type": "number",
                    "minimum": 0,
                    "maximum": 1,
                    "description": "The new importance score in [0,1], or omit to leave unchanged."
                }
            },
            "required": ["recordId"]
        }
        """).RootElement;

    private readonly IMemoryStore _store;
    private readonly string _userId;

    /// <summary>
    /// Initialises a new <see cref="MemoryUpdateTool"/>.
    /// </summary>
    /// <param name="store">The memory store for update operations.</param>
    /// <param name="userId">The owning user — constrains the update to that user's records.</param>
    internal MemoryUpdateTool(IMemoryStore store, string userId)
    {
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._userId = userId ?? throw new ArgumentNullException(nameof(userId));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => new(
        Name: "Memory_Update",
        Description: "Updates the Value and/or Importance of an existing record. Only supplied parameters are changed.",
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

        string? newValue = null;
        if (input.TryGetProperty("newValue", out JsonElement valueEl)
            && valueEl.ValueKind == JsonValueKind.String)
        {
            newValue = valueEl.GetString();
        }

        double? newImportance = null;
        if (input.TryGetProperty("newImportance", out JsonElement impEl)
            && impEl.ValueKind == JsonValueKind.Number)
        {
            newImportance = impEl.GetDouble();
        }

        if (newValue is null && newImportance is null)
        {
            return new ToolResult("At least one of newValue or newImportance must be supplied.", IsError: true);
        }

        try
        {
            var result = await this._store.UpdateRecordAsync(recordId, this._userId, newValue, newImportance, ct)
                .ConfigureAwait(false);

            if (result is null)
            {
                return new ToolResult($"Record {recordId} not found.", IsError: true);
            }

            return new ToolResult($"Updated record {recordId}.");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Update failed: {ex.Message}", IsError: true);
        }
    }
}
