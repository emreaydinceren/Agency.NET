using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agency.Harness.Console;

/// <summary>
/// Persists the enabled/disabled state toggled via <c>/mcp-toggle</c> so it survives restarts,
/// mirroring <see cref="DefaultModelConfiguration"/>'s read-modify-write pattern.
/// </summary>
internal static class McpServerConfiguration
{
    /// <summary>
    /// Sets <c>Enabled</c> to <paramref name="enabled"/> on the <c>Mcp:Servers</c> entry named
    /// <paramref name="serverName"/> (matched case-insensitively) on disk at <paramref name="appSettingsPath"/>.
    /// Deliberately does not round-trip the full <see cref="Tools.McpClientOptions"/> object: by the time the
    /// app is running, <see cref="McpConfigResolver.Expand"/> has substituted a machine-specific
    /// <c>${RepoRoot}</c> path and the live <c>${GitHubToken}</c> secret into it in place, so serializing it
    /// back would destroy portability and write a credential into the committed file. Does nothing —
    /// silently, without throwing — if the file, the <c>Mcp</c> section, the <c>Servers</c> array, or the
    /// named server is missing.
    /// </summary>
    /// <param name="appSettingsPath">Full path to the appsettings.json file to persist the toggle into.</param>
    /// <param name="serverName">The MCP server name to update, matched case-insensitively.</param>
    /// <param name="enabled">The new <c>Enabled</c> value to persist for the server.</param>
    internal static void Persist(string appSettingsPath, string serverName, bool enabled)
    {
        if (!File.Exists(appSettingsPath))
        {
            return;
        }

        JsonNode root = JsonNode.Parse(
            File.ReadAllText(appSettingsPath),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
            ?? new JsonObject();

        if (root["Mcp"]?["Servers"] is not JsonArray servers)
        {
            return;
        }

        JsonObject? server = servers
            .OfType<JsonObject>()
            .FirstOrDefault(entry =>
                entry["Name"] is JsonValue name
                && name.TryGetValue(out string? value)
                && value.Equals(serverName, StringComparison.OrdinalIgnoreCase));

        if (server is null)
        {
            return;
        }

        server["Enabled"] = enabled;

        File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
