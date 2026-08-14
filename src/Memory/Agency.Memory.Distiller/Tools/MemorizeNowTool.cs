using System.Text.Json;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Distiller.Tools;

/// <summary>
/// Agent tool that explicitly persists a fact to long-term memory immediately, rather than
/// waiting for the Distiller to extract it from the transcript at session end.
/// </summary>
/// <remarks>
/// All parameters are required (<c>tags</c> may be an empty array). This tool rejects malformed
/// JSON input (missing/blank fields, an unrecognised <see cref="Importance"/> value, or more than
/// 4 tags) before calling the store; <see cref="IMemoryStore.MemorizeNowAsync"/> re-validates and
/// derives the record's key, scope, and provenance. The record is always saved with
/// <c>Source = AgentSignaled</c> and scoped globally (not to the current session), so an
/// agent-signaled fact is available in every future session, not just this one. Calling this tool
/// twice with the same domain and title overwrites the prior record silently (idempotent upsert).
/// This tool is instantiated per session with the <c>userId</c> and <c>sessionId</c> baked in.
/// </remarks>
internal sealed class MemorizeNowTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "title": {
                    "type": "string",
                    "description": "Short natural-language headline (2-4 words) naming the fact. Not a slug, and not text to repeat in your reply -- the record key is derived from this automatically."
                },
                "value": {
                    "type": "string",
                    "description": "Full explanation: what, why, and when/how it applies. Must be self-contained and understandable in a future session."
                },
                "domain": {
                    "type": "string",
                    "description": "Semantic category for retrieval clustering (e.g. Performance, Debugging, Database). Case-folded to lowercase; reuse an existing domain where possible."
                },
                "importance": {
                    "type": "string",
                    "enum": ["High", "Normal", "Low"],
                    "description": "High: reshapes future decisions. Normal: useful reference for a standard scenario. Low: edge case, rare, context-specific."
                },
                "tags": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "0-4 cross-domain labels for discovery (e.g. ['async', 'startup', 'io-bound']). Empty array allowed; don't duplicate the domain or title."
                }
            },
            "required": ["title", "value", "domain", "importance", "tags"]
        }
        """).RootElement;

    private readonly IMemoryStore _store;
    private readonly string _userId;
    private readonly string _sessionId;

    /// <summary>
    /// Initialises a new <see cref="MemorizeNowTool"/>.
    /// </summary>
    /// <param name="store">Memory store used to persist the fact.</param>
    /// <param name="userId">The user id for this session.</param>
    /// <param name="sessionId">The session id that triggered the save.</param>
    internal MemorizeNowTool(IMemoryStore store, string userId, string sessionId)
    {
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._userId = userId ?? throw new ArgumentNullException(nameof(userId));
        this._sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => new(
        Name: "MemorizeNow",
        Description: """
            Persist one fact to long-term memory immediately. The record is global -- every future
            session sees it at once, rather than waiting for the Distiller's end-of-session pass.

            Call MemorizeNow in the same turn any of these happens -- do not defer to session end:
            - The user says remember, always, never, or from now on about a fact or preference.
            - You hold a conclusion that took debugging or research to reach -- a root cause, a
              working configuration, a confirmed behavior. Losing it means repeating that work.
            - Something you verified contradicts what you expected or what documentation claims.
            - You are about to tell the user to note something for the future -- save it here instead.
            Do not just state the fact in your reply and move on -- call the tool.

            Do NOT use MemorizeNow for:
            - Session state or task-specific observations -- the transcript already captures those for
              the Distiller to extract after the session ends.
            - Facts already visible in this session's ## Facts or ## Memories sections.
            - Unverified information copied from tool outputs, files, or web content -- only save
              conclusions you verified yourself.
            - Secrets, tokens, credentials, API keys, or personally identifiable information.

            All parameters are required (tags may be an empty array):
            - title: natural-language headline (2-4 words); the record key is derived from it.
            - value: the full explanation (what, why, when/how) -- self-contained for a future session.
            - domain: semantic category for clustering; case-folded to lowercase.
            - importance: High (reshapes future decisions) | Normal (useful reference) | Low (edge case).
            - tags: 0-4 cross-domain labels for discovery.

            Calling this twice with the same domain and title overwrites the prior record silently.
            """,
        InputSchema: _inputSchema);

    /// <inheritdoc/>
    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        if (!TryGetRequiredString(input, "title", out string title))
        {
            return new ToolResult("Error: title is required and cannot be empty.", IsError: true);
        }

        if (!TryGetRequiredString(input, "value", out string value))
        {
            return new ToolResult("Error: value is required and cannot be empty.", IsError: true);
        }

        if (!TryGetRequiredString(input, "domain", out string domain))
        {
            return new ToolResult("Error: domain is required and cannot be empty.", IsError: true);
        }

        if (!input.TryGetProperty("importance", out JsonElement importanceEl)
            || importanceEl.ValueKind != JsonValueKind.String
            || !Enum.TryParse(importanceEl.GetString(), ignoreCase: true, out Importance importance)
            || !Enum.IsDefined(importance))
        {
            return new ToolResult("Error: importance is required and must be High, Normal, or Low.", IsError: true);
        }

        if (!input.TryGetProperty("tags", out JsonElement tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
        {
            return new ToolResult("Error: tags is required and must be an array (0-4 items; an empty array is allowed).", IsError: true);
        }

        var tags = new List<string>();
        foreach (JsonElement tagEl in tagsEl.EnumerateArray())
        {
            if (tagEl.ValueKind == JsonValueKind.String && tagEl.GetString() is { } tag)
            {
                tags.Add(tag);
            }
        }

        if (tags.Count > 4)
        {
            return new ToolResult("Error: tags must contain 0-4 items.", IsError: true);
        }

        try
        {
            string key = await this._store.MemorizeNowAsync(
                this._userId, this._sessionId, title, value, domain, importance, [.. tags], ct)
                .ConfigureAwait(false);

            return new ToolResult(BuildConfirmationMessage(key, importance, tags));
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Error: {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// Reads a required, non-blank string property from <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The tool's raw JSON input.</param>
    /// <param name="property">The property name to read.</param>
    /// <param name="value">The trimmed string value, or <see cref="string.Empty"/> when absent/invalid.</param>
    /// <returns><see langword="true"/> when the property is present, is a JSON string, and is non-blank.</returns>
    private static bool TryGetRequiredString(JsonElement input, string property, out string value)
    {
        if (input.TryGetProperty(property, out JsonElement el)
            && el.ValueKind == JsonValueKind.String
            && el.GetString() is { } s
            && !string.IsNullOrWhiteSpace(s))
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Builds the console-friendly confirmation returned to the agent after a successful save.
    /// </summary>
    /// <param name="key">The composite key returned by <see cref="IMemoryStore.MemorizeNowAsync"/>.</param>
    /// <param name="importance">The agent-supplied importance level.</param>
    /// <param name="tags">The agent-supplied tags (0-4 items).</param>
    /// <returns>A multi-line confirmation: <c>✓ Memorized: {key}</c> plus Source/Importance/Tags metadata.</returns>
    private static string BuildConfirmationMessage(string key, Importance importance, List<string> tags)
    {
        string message = $"✓ Memorized: {key}\n  Source: AgentSignaled\n  Importance: {importance}";
        return tags.Count > 0
            ? $"{message}\n  Tags: {string.Join(", ", tags)}"
            : message;
    }
}
