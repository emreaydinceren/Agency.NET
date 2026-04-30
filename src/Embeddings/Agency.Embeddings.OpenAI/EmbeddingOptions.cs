namespace Agency.Embeddings.OpenAI;

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

}