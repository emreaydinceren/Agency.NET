namespace Agency.Embeddings;

/// <summary>
/// Options used to configure embedding generation.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>
    /// The configuration section name for embedding options.
    /// </summary>
    public const string SectionName = "Embedding";

    /// <summary>
    /// Gets or sets the service base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the embedding model identifier.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// API key sent in the Authorization header. LM Studio does not validate this,
    /// but the OpenAI SDK requires a non-empty value. Defaults to "lmstudio".
    /// </summary>
    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets default settings for a local LM Studio instance.
    /// </summary>
    public static EmbeddingOptions LMStudioDefaults => new EmbeddingOptions
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ModelId = "text-embedding-qwen3-embedding-8b",
        ApiKey = "lmstudio"
    };
}