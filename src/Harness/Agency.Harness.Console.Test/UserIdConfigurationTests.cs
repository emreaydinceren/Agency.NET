using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="UserIdConfiguration"/>, which resolves and persists a stable per-installation
/// user id under <c>Agent:UserId</c>.
/// </summary>
public sealed class UserIdConfigurationTests
{
    private static IConfiguration InMemory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void WhenAlreadyConfigured_ReturnsExistingAndDoesNotGenerate()
    {
        IConfiguration config = InMemory(("Agent:UserId", "preset-id"));

        string id = UserIdConfiguration.EnsureUserId(
            config,
            appSettingsPath: "does-not-exist.json",
            idFactory: () => throw new InvalidOperationException("idFactory must not be called when a UserId is already configured."));

        Assert.Equal("preset-id", id);
    }

    [Fact]
    public void WhenMissing_GeneratesPersistsToFileAndSurfacesToConfig()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agency-userid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, """{"Agent":{"DefaultModel":"m"}}""");

        try
        {
            IConfiguration config = InMemory();

            string id = UserIdConfiguration.EnsureUserId(config, path, () => "fixed-id");

            // Returned, and surfaced to the in-memory configuration for this run.
            Assert.Equal("fixed-id", id);
            Assert.Equal("fixed-id", config[UserIdConfiguration.ConfigKey]);

            // Persisted to disk under Agent:UserId, preserving the existing Agent property.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var agent = doc.RootElement.GetProperty("Agent");
            Assert.Equal("fixed-id", agent.GetProperty("UserId").GetString());
            Assert.Equal("m", agent.GetProperty("DefaultModel").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
