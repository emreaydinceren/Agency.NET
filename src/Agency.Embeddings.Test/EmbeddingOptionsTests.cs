namespace Agency.Embeddings.Test;

public sealed class EmbeddingOptionsTests
{
    [Fact]
    public void SectionName_IsEmbedding()
    {
        Assert.Equal("Embedding", EmbeddingOptions.SectionName);
    }

    [Fact]
    public void DefaultBaseUrl_PointsToLocalLmStudio()
    {
        var options = EmbeddingOptions.LMStudioDefaults;

        Assert.Equal("http://127.0.0.1:1234/v1", options.BaseUrl);
    }

    [Fact]
    public void DefaultModelId_IsQwen3VlEmbedding8B()
    {
        var options = EmbeddingOptions.LMStudioDefaults;

        Assert.Equal("text-embedding-qwen3-embedding-8b", options.ModelId);
    }

    [Fact]
    public void DefaultApiKey_IsLmStudio()
    {
        var options = EmbeddingOptions.LMStudioDefaults;

        Assert.Equal("lmstudio", options.ApiKey);
    }
}