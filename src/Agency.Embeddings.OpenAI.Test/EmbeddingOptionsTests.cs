using Agency.Embeddings.OpenAI;

namespace Agency.Embeddings.OpenAI.Test;

/// <summary>
/// Tests for <see cref="Agency.Embeddings.OpenAI.EmbeddingOptions"/> defaults.
/// </summary>
public sealed class EmbeddingOptionsTests
{
    /// <summary>
    /// Verifies the configuration section name.
    /// </summary>
    [Fact]
    public void SectionName_IsEmbedding()
    {
        Assert.Equal("Embedding", EmbeddingOptions.SectionName);
    }

    /// <summary>
    /// Verifies the default LM Studio base URL.
    /// </summary>
    [Fact]
    public void DefaultBaseUrl_PointsToLocalLmStudio()
    {
        var options = EmbeddingOptions.LMStudioDefaults;

        Assert.Equal("http://llm-host.example:1234/v1", options.BaseUrl);
    }

    /// <summary>
    /// Verifies the default LM Studio model identifier.
    /// </summary>
    [Fact]
    public void DefaultModelId_IsQwen3VlEmbedding8B()
    {
        var options = EmbeddingOptions.LMStudioDefaults;

        Assert.Equal("text-embedding-qwen3-embedding-8b", options.ModelId);
    }

    /// <summary>
    /// Verifies the default LM Studio API key.
    /// </summary>
    [Fact]
    public void DefaultApiKey_IsLmStudio()
    {
        var options = EmbeddingOptions.LMStudioDefaults;

        Assert.Equal("lmstudio", options.ApiKey);
    }
}