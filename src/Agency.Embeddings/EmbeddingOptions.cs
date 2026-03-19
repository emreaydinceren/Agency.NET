namespace Agency.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public string? BaseUrl { get; set; }
    public string? ModelId { get; set; }

    /// <summary>
    /// API key sent in the Authorization header. LM Studio does not validate this,
    /// but the OpenAI SDK requires a non-empty value. Defaults to "lmstudio".
    /// </summary>
    public string? ApiKey { get; set; }

    public static EmbeddingOptions LMStudioDefaults => new EmbeddingOptions
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ModelId = "text-embedding-qwen3-embedding-8b",
        ApiKey = "lmstudio"
    };
}