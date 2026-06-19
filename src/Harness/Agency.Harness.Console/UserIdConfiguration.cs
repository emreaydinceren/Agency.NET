using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agency.Harness.Console;

/// <summary>
/// Resolves a stable, per-installation user identity used to partition memory.
/// </summary>
/// <remarks>
/// If <c>Agent:UserId</c> is already configured it is used unchanged. Otherwise a new id is generated, written
/// back into the on-disk <c>appsettings.json</c> under <c>Agent:UserId</c>, and reused on every subsequent run.
/// The resolved id flows into <see cref="Contexts.UserSpecificContext.Id"/> and is what
/// <see cref="UserIdPlaceholderHook"/> substitutes for the <c>{userId}</c> placeholder in tool calls.
/// </remarks>
internal static class UserIdConfiguration
{
    internal const string ConfigKey = "Agent:UserId";

    /// <summary>
    /// Ensures a user id is present in <paramref name="configuration"/>, generating and persisting one to
    /// <paramref name="appSettingsPath"/> when absent. The id is also written into the in-memory configuration
    /// so options bound during this run observe it without a reload.
    /// </summary>
    /// <param name="configuration">The application configuration to read from and update in memory.</param>
    /// <param name="appSettingsPath">Full path to the appsettings.json file to persist a generated id into.</param>
    /// <param name="idFactory">Factory for a new id; injected so tests can supply a deterministic value.</param>
    /// <returns>The resolved user id.</returns>
    internal static string EnsureUserId(IConfiguration configuration, string appSettingsPath, Func<string> idFactory)
    {
        string? existing = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(existing) == false)
        {
            return existing;
        }

        string id = idFactory();
        Persist(appSettingsPath, id);

        // Surface to the in-memory configuration so AgentOptions binds it on this run.
        configuration[ConfigKey] = id;
        return id;
    }

    private static void Persist(string appSettingsPath, string id)
    {
        if (File.Exists(appSettingsPath) == false)
        {
            // No file to persist into (e.g. config came purely from other providers); the in-memory
            // value still applies for this run, but it will not survive a restart.
            return;
        }

        JsonNode root = JsonNode.Parse(
            File.ReadAllText(appSettingsPath),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
            ?? new JsonObject();

        if (root["Agent"] is JsonObject agent)
        {
            agent["UserId"] = id;
        }
        else
        {
            root["Agent"] = new JsonObject { ["UserId"] = id };
        }

        File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
