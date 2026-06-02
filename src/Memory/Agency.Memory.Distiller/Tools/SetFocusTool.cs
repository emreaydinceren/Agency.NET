using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Distiller.Tools;

/// <summary>
/// Agent tool that updates <see cref="Context.Focus"/> to bias retrieval toward
/// a particular task domain (Spec §6.7.1).
/// </summary>
/// <remarks>
/// The tool description dynamically lists the user's existing domain values so
/// the agent reuses established vocabulary. Setting the same focus twice is a no-op
/// and returns the prior focus values.
/// </remarks>
internal sealed class SetFocusTool : ITool
{
    private static readonly JsonElement _baseInputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "title": {
                    "type": "string",
                    "description": "Short title for the current focus area (e.g., 'Auth Debugging')."
                },
                "domain": {
                    "type": "string",
                    "description": "Semantic domain for the focus (should match one of the known domains listed in this tool's description where possible)."
                },
                "tags": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Tags associated with the current focus (0–5 short tags)."
                }
            }
        }
        """).RootElement;

    private readonly IMemoryStore _store;
    private readonly string _userId;
    private readonly Func<Context> _contextAccessor;

    /// <summary>
    /// Initialises a new <see cref="SetFocusTool"/>.
    /// </summary>
    /// <param name="store">Memory store used to query existing domains for the description.</param>
    /// <param name="userId">The user id, used to enumerate known domains.</param>
    /// <param name="contextAccessor">Returns the current session context.</param>
    internal SetFocusTool(
        IMemoryStore store,
        string userId,
        Func<Context> contextAccessor)
    {
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._userId = userId ?? throw new ArgumentNullException(nameof(userId));
        this._contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition
    {
        get
        {
            // Build the dynamic part of the description synchronously by querying the store.
            // In v1 the full corpus is listed; v2 will use top-N by frequency (Spec §O5).
            string[] knownDomains = this.GetKnownDomainsSync();
            string domainList = knownDomains.Length > 0
                ? string.Join(", ", knownDomains)
                : "(none yet)";

            return new ToolDefinition(
                Name: "SetFocus",
                Description: $"Update the session focus to bias memory retrieval toward a particular task domain. " +
                             $"Known domains for this user: [{domainList}]. " +
                             "Returns the previous focus values.",
                InputSchema: _baseInputSchema);
        }
    }

    /// <inheritdoc/>
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        string? title = GetString(input, "title");
        string? domain = GetString(input, "domain");
        List<string>? tags = GetStringArray(input, "tags");

        Context ctx = this._contextAccessor();
        FocusContext currentFocus = ctx.Focus;

        // Idempotency check — if same values, no-op.
        bool sameTitle = string.Equals(currentFocus.Title, title, StringComparison.Ordinal);
        bool sameDomain = string.Equals(currentFocus.Domain, domain, StringComparison.Ordinal);
        bool sameTags = tags is null && currentFocus.Tags.Count == 0
            || (tags is not null && currentFocus.Tags.SequenceEqual(tags, StringComparer.Ordinal));

        string priorJson = JsonSerializer.Serialize(new
        {
            title = currentFocus.Title,
            domain = currentFocus.Domain,
            tags = currentFocus.Tags,
        });

        if (sameTitle && sameDomain && sameTags)
        {
            return Task.FromResult(new ToolResult($"Focus unchanged. Prior focus: {priorJson}"));
        }

        ctx.Focus = new FocusContext
        {
            Title = title,
            Domain = domain,
            Tags = tags ?? currentFocus.Tags,
        };

        return Task.FromResult(new ToolResult($"Focus updated. Prior focus: {priorJson}"));
    }

    private string[] GetKnownDomainsSync()
    {
        try
        {
            // Fire-and-forget using a ValueTask — acceptable for description generation.
            IReadOnlyList<Record> records = this._store
                .GetAllForUserAsync(this._userId, CancellationToken.None)
                .GetAwaiter().GetResult();

            return records
                .Select(static r => r.Domain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? GetString(JsonElement input, string property) =>
        input.TryGetProperty(property, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static List<string>? GetStringArray(JsonElement input, string property)
    {
        if (!input.TryGetProperty(property, out JsonElement el)
            || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }
}
