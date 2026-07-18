using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agency.Harness.Console;

/// <summary>
/// Persists the client/model picked via <see cref="Commands.ModelsCommand"/> as the new
/// startup default, mirroring <see cref="UserIdConfiguration"/>'s read-modify-write pattern.
/// </summary>
internal static class DefaultModelConfiguration
{
    /// <summary>
    /// Writes <paramref name="clientName"/>/<paramref name="model"/> into <c>Agent:DefaultClientName</c>/
    /// <c>Agent:DefaultModel</c> on disk at <paramref name="appSettingsPath"/>, and into
    /// <paramref name="configuration"/> so the running process reflects the new default immediately.
    /// </summary>
    /// <param name="configuration">The application configuration to update in memory.</param>
    /// <param name="appSettingsPath">Full path to the appsettings.json file to persist the default into.</param>
    /// <param name="clientName">The newly selected LLM client name.</param>
    /// <param name="model">The newly selected model id.</param>
    internal static void Persist(IConfiguration configuration, string appSettingsPath, string clientName, string model)
    {
        if (File.Exists(appSettingsPath))
        {
            JsonNode root = JsonNode.Parse(
                File.ReadAllText(appSettingsPath),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
                ?? new JsonObject();

            if (root["Agent"] is JsonObject agent)
            {
                agent["DefaultClientName"] = clientName;
                agent["DefaultModel"] = model;
            }
            else
            {
                root["Agent"] = new JsonObject { ["DefaultClientName"] = clientName, ["DefaultModel"] = model };
            }

            File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // Surface to the in-memory configuration so AgentOptions reflects the switch without a restart.
        configuration["Agent:DefaultClientName"] = clientName;
        configuration["Agent:DefaultModel"] = model;
    }
}
