using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agency.Embeddings.OpenAI.Test;

/// <summary>
/// Tests for <see cref="Agency.Embeddings.OpenAI.EmbeddingOptions"/> configuration loading.
/// </summary>
public sealed class EmbeddingOptionsTests
{
    private static readonly EmbeddingOptions Sut = LoadFromConfig();

    private static EmbeddingOptions LoadFromConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddSharedConfiguration("shared-test-appsettings.json")
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<EmbeddingOptionsTests>(optional: true)
            .AddEnvironmentVariables()
            .AddPlaceholderResolver()
            .Build();

        return Options.Create(new EmbeddingOptions
        {
            BaseUrl = configuration[$"{EmbeddingOptions.SectionName}:BaseUrl"],
            ModelId = configuration[$"{EmbeddingOptions.SectionName}:ModelId"],
            ApiKey = configuration[$"{EmbeddingOptions.SectionName}:ApiKey"],
        }).Value;
    }

    /// <summary>
    /// Verifies the configuration section name.
    /// </summary>
    [Fact]
    public void SectionName_IsEmbedding()
    {
        Assert.Equal("Embedding", EmbeddingOptions.SectionName);
    }

    /// <summary>
    /// Verifies that the configured LM Studio base URL is loaded correctly.
    /// </summary>
    [Fact]
    public void BaseUrl_IsLoadedFromConfig()
    {
        Assert.False(string.IsNullOrEmpty(Sut.BaseUrl), "BaseUrl should be loaded from configuration.");
    }

    /// <summary>
    /// Verifies that the configured LM Studio model identifier is loaded correctly.
    /// </summary>
    [Fact]
    public void ModelId_IsLoadedFromConfig()
    {
        Assert.Equal("text-embedding-qwen3-embedding-0.6b", Sut.ModelId);
    }

    /// <summary>
    /// Verifies that the configured LM Studio API key is loaded correctly.
    /// </summary>
    [Fact]
    public void ApiKey_IsLoadedFromConfig()
    {
        Assert.Equal("lm-studio", Sut.ApiKey);
    }
}
