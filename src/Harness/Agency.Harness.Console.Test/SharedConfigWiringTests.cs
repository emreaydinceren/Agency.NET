using Microsoft.Extensions.Configuration;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Verifies that the shared-config wiring adopted in Program.cs resolves the
/// <c>${LLmClients:…}</c> and <c>${ConnectionStrings:…}</c> tokens to their
/// canonical values: the BaseUrl/ApiKey keys come from <c>shared-appsettings.json</c>,
/// while <c>ConnectionStrings:PostgreSql</c> comes from the <c>AgencySecrets</c>
/// user-secrets vault (never a committed appsettings file).
///
/// The first in-memory layer stands in for the merged seed the resolver snapshots
/// (shared file + user secrets), so the test exercises placeholder expansion without
/// depending on file-copy propagation or a live secret store.
/// </summary>
public sealed class SharedConfigWiringTests
{
    /// <summary>
    /// Placeholder tokens referencing the shared <c>LLmClients</c> and <c>ConnectionStrings</c>
    /// keys are expanded to their canonical values.
    /// </summary>
    [Fact]
    public void AddPlaceholderResolver_Expands_SharedConfig_Tokens()
    {
        // Layer 1 — merged-seed equivalent: BaseUrl/ApiKey from shared-appsettings.json,
        //           ConnectionStrings:PostgreSql from the AgencySecrets user-secrets vault.
        // Layer 2 — appsettings.json equivalent (refactored): values reference the shared keys.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LLmClients:OpenAI:BaseUrl"] = "http://llm.test:1234/v1",
                ["LLmClients:Claude:BaseUrl"] = "http://llm.test:1234",
                ["LLmClients:ApiKey"] = "lm-studio",
                ["ConnectionStrings:PostgreSql"] = "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password",
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:LLmClients:0:BaseUrl"] = "${LLmClients:OpenAI:BaseUrl}",
                ["Agent:LLmClients:0:ApiKey"] = "${LLmClients:ApiKey}",
                ["Agent:LLmClients:1:BaseUrl"] = "${LLmClients:Claude:BaseUrl}",
                ["Agent:LLmClients:1:ApiKey"] = "${LLmClients:ApiKey}",
                ["Embedding:BaseUrl"] = "${LLmClients:OpenAI:BaseUrl}",
                ["Embedding:ApiKey"] = "${LLmClients:ApiKey}",
                ["ConnectionStrings:VectorStorePostgreSql"] = "${ConnectionStrings:PostgreSql}",
            })
            .AddPlaceholderResolver()
            .Build();

        Assert.Equal("http://llm.test:1234/v1", config["Agent:LLmClients:0:BaseUrl"]);
        Assert.Equal("lm-studio", config["Agent:LLmClients:0:ApiKey"]);
        Assert.Equal("http://llm.test:1234", config["Agent:LLmClients:1:BaseUrl"]);
        Assert.Equal("lm-studio", config["Agent:LLmClients:1:ApiKey"]);
        Assert.Equal("http://llm.test:1234/v1", config["Embedding:BaseUrl"]);
        Assert.Equal("lm-studio", config["Embedding:ApiKey"]);
        Assert.Equal(
            "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password",
            config["ConnectionStrings:VectorStorePostgreSql"]);
    }
}
