using Agency.Memory.Sql.Postgres;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Adapts <see cref="WatermarkRepository"/> to the <see cref="IWatermarkStore"/> interface.
/// </summary>
internal sealed class WatermarkStoreAdapter : IWatermarkStore
{
    private readonly WatermarkRepository _repository;

    /// <summary>
    /// Initialises a new <see cref="WatermarkStoreAdapter"/>.
    /// </summary>
    /// <param name="repository">The backing Postgres watermark repository.</param>
    internal WatermarkStoreAdapter(WatermarkRepository repository)
    {
        this._repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc/>
    public Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default) =>
        this._repository.GetAsync(userId, sessionId, ct);

    /// <inheritdoc/>
    public Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default) =>
        this._repository.AdvanceAsync(userId, sessionId, candidate, ct);
}
