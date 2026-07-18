using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="DefaultModelConfiguration"/>, which persists the client/model picked
/// via <c>/Model</c> as the new startup default under <c>Agent:DefaultClientName</c>/<c>Agent:DefaultModel</c>.
/// </summary>
public sealed class DefaultModelConfigurationTests
{
    private static IConfiguration InMemory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    /// <summary>
    /// Persisting writes the new client/model to disk under the existing <c>Agent</c> object,
    /// preserving unrelated settings, and surfaces the change into the in-memory configuration.
    /// </summary>
    [Fact]
    public void Persist_WritesToFileAndSurfacesToConfig()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agency-defaultmodel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, """{"Agent":{"DefaultClientName":"old-client","DefaultModel":"old-model","UserId":"kept-id"}}""");

        try
        {
            IConfiguration config = InMemory();

            DefaultModelConfiguration.Persist(config, path, "new-client", "new-model");

            // Surfaced to the in-memory configuration for this run.
            Assert.Equal("new-client", config["Agent:DefaultClientName"]);
            Assert.Equal("new-model", config["Agent:DefaultModel"]);

            // Persisted to disk, preserving unrelated existing Agent properties.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var agent = doc.RootElement.GetProperty("Agent");
            Assert.Equal("new-client", agent.GetProperty("DefaultClientName").GetString());
            Assert.Equal("new-model", agent.GetProperty("DefaultModel").GetString());
            Assert.Equal("kept-id", agent.GetProperty("UserId").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// When the target file does not exist, persisting is a no-op on disk, but the in-memory
    /// configuration still reflects the switch for the remainder of this run.
    /// </summary>
    [Fact]
    public void Persist_WhenFileMissing_StillSurfacesToConfig()
    {
        IConfiguration config = InMemory();

        DefaultModelConfiguration.Persist(config, "does-not-exist.json", "new-client", "new-model");

        Assert.Equal("new-client", config["Agent:DefaultClientName"]);
        Assert.Equal("new-model", config["Agent:DefaultModel"]);
    }
}
