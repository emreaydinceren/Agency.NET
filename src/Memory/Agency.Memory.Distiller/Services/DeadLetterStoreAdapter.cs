using Agency.Memory.Sql.Postgres;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Adapts <see cref="DeadLetterRepository"/> to the <see cref="IDeadLetterStore"/> interface.
/// </summary>
internal sealed class DeadLetterStoreAdapter : IDeadLetterStore
{
    private readonly DeadLetterRepository _repository;

    /// <summary>
    /// Initialises a new <see cref="DeadLetterStoreAdapter"/>.
    /// </summary>
    /// <param name="repository">The backing Postgres dead-letter repository.</param>
    internal DeadLetterStoreAdapter(DeadLetterRepository repository)
    {
        this._repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc/>
    public Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception error,
        CancellationToken ct = default) =>
        this._repository.WriteAsync(userId, sessionId, jobKind, payload, error, ct);
}
