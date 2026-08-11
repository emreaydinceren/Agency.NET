using Agency.Harness.Hooks;
using Agency.Llm.Common.Tools;
using System.Text.Json;

namespace Agency.Harness.Console;

/// <summary>
/// An <c>OnSessionStarted</c> hook that primes every turn with a lightweight index of what's already
/// stored in the "memory" MCP server (domain/key pairs only, no values), so the model doesn't have to
/// gamble on whether calling <c>recall</c> is worthwhile — it can see what's available up front and
/// fetch only the entries relevant to the current turn. Also injects write-side guidance telling the
/// model when to proactively offer memorization (preferences, patterns, durable facts the user shares).
/// </summary>
/// <remarks>
/// Deliberately does not inject the stored values themselves: that would re-introduce the unbounded
/// prompt growth this hook exists to avoid. Only <c>list_global_keys</c> (cheap, no values) is called
/// automatically; <c>recall</c> remains a model-invoked tool for fetching a specific value on demand.
/// </remarks>
internal static class MemoryIndexHook
{
    private const string FactPrefix = "Memory (persists across sessions): ";
    private const string WritePolicy =
        "save durable facts about this user with memorize as you learn them, and fetch them with recall. " +
        "When the user shares a clear preference, pattern, technique, or durable fact that would be " +
        "regrettable to lose or that recurs across sessions, offer to memorize it. ";
    internal const string ListGlobalKeysToolName = "list_global_keys";

    /// <summary>
    /// Builds the hook. Returns empty (no-op) hooks when <paramref name="listGlobalKeys"/> is
    /// <see langword="null"/> (the "memory" MCP server isn't configured or failed to connect), so the
    /// feature degrades gracefully — matching the rest of the console host's "never let MCP take down
    /// the app" behavior.
    /// </summary>
    internal static AgentHooks Build(ITool? listGlobalKeys)
    {
        if (listGlobalKeys is null)
        {
            return AgentHooks.None;
        }

        return new AgentHooks
        {
            OnSessionStarted = async (hookCtx, ct) =>
            {
                // The tool is invoked directly rather than through the registry, so an explicit check is
                // needed to honour a /mcp-toggle that disabled the memory server for this session. Strip
                // any fact from an earlier turn so the index disappears on the very next turn.
                if (!hookCtx.AgentContext.Tools.Registry.ListDefinitions()
                        .Any(d => d.Name == ListGlobalKeysToolName))
                {
                    hookCtx.AgentContext.Knowledge = hookCtx.AgentContext.Knowledge with
                    {
                        Facts = [.. hookCtx.AgentContext.Knowledge.Facts.Where(
                            f => !f.StartsWith(FactPrefix, StringComparison.Ordinal))],
                    };
                    return;
                }

                string? userId = hookCtx.AgentContext.User.Id;
                if (string.IsNullOrEmpty(userId))
                {
                    return;
                }

                string? fact;
                try
                {
                    fact = await BuildIndexFactAsync(listGlobalKeys, userId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The MCP subprocess/IPC can fail independently of the agent turn (crashed server,
                    // broken pipe); losing the index for one turn is not worth surfacing as a turn failure.
                    return;
                }

                // OnSessionStarted fires on every turn (once per ChatAsync call), not just once per
                // session, so the previous turn's index entry — if any — must be replaced, not appended
                // to, or it would duplicate on every turn.
                List<string> facts = [.. hookCtx.AgentContext.Knowledge.Facts.Where(
                    f => !f.StartsWith(FactPrefix, StringComparison.Ordinal))];
                if (fact is not null)
                {
                    facts.Add(fact);
                }

                hookCtx.AgentContext.Knowledge = hookCtx.AgentContext.Knowledge with { Facts = facts };
            },
        };
    }

    private static async Task<string?> BuildIndexFactAsync(ITool listGlobalKeys, string userId, CancellationToken ct)
    {
        JsonElement input = JsonSerializer.SerializeToElement(new { scope = new { userId } });
        ToolResult result = await listGlobalKeys.InvokeAsync(input, ct).ConfigureAwait(false);
        if (result.IsError)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(result.Content);
        var pairs = new List<string>();
        foreach (JsonProperty domain in doc.RootElement.EnumerateObject())
        {
            if (domain.Value.TryGetProperty("Keys", out JsonElement keys))
            {
                foreach (JsonElement key in keys.EnumerateArray())
                {
                    pairs.Add($"{domain.Name}|{key.GetString()}");
                }
            }
        }

        // An empty store still gets a fact. The index is what would be clutter for a fresh user; the
        // write-side instruction is the opposite — with no keys stored and nothing in the system prompt
        // telling the model to save, "remember things about me" lives only in the memorize tool's own
        // description, which the model only reads once it has already decided to reach for the tool.
        return FactPrefix + WritePolicy + (pairs.Count > 0
            ? "Already stored — call recall(domain, key) for a value: " + string.Join(", ", pairs)
            : "Nothing is stored for this user yet.");
    }
}
