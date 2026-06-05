namespace Agency.Memory.Common.Storage;

/// <summary>
/// Provisions the storage schema required by the Agency memory system, implemented by each provider.
/// </summary>
/// <remarks>
/// Lives in <c>Agency.Memory.Common</c> so a host can resolve and run schema initialisation at startup
/// without referencing the concrete provider selected at composition time.
/// </remarks>
public interface IMemorySchemaInitializer
{
    /// <summary>
    /// Provisions all required tables and indexes. Safe to call on every application start.
    /// </summary>
    /// <param name="embeddingDim">The embedding dimension; must match any pre-existing schema.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the schema was previously initialised with a different embedding dimension.
    /// </exception>
    Task InitializeAsync(int embeddingDim, CancellationToken ct = default);
}
