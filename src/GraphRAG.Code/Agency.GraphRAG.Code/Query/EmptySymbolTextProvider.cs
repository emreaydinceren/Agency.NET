using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Default symbol text provider used when no raw-source loader is configured.
/// </summary>
public sealed class EmptySymbolTextProvider : ISymbolTextProvider
{
    /// <inheritdoc />
    public Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
