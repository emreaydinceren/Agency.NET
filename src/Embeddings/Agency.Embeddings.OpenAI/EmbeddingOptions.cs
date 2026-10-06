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

    /// <summary>
    /// Gets or sets the number of dimensions produced by the embedding model.
    /// Required when using a SQLite vector store so the schema is created with the correct column width.
    /// </summary>
    public int? Dimensions { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of inputs sent in one embeddings request. Larger batches are split
    /// into sequential requests so a single big document cannot flood the server. Defaults to 32.
    /// </summary>
    public int MaxBatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets how many times a failed request is retried when the failure is transient (429, 5xx, a timeout or a
    /// connection error); <see langword="null"/> keeps the SDK default of 3. Permanent errors such as 400 are never retried.
    /// </summary>
    public int? MaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the wait before the first retry in milliseconds; each further retry waits twice as long, up to 30 seconds.
    /// The default 0 keeps the SDK's near-immediate retries, which suit an interactive caller that should fail fast.
    /// </summary>
    public int RetryDelayMs { get; set; }
}