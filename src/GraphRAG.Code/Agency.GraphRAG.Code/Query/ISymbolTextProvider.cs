using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Loads raw source text for a symbol when query context needs exact code.
/// </summary>
public interface ISymbolTextProvider
{
    /// <summary>
    /// Loads the raw source text for a symbol.
    /// </summary>
    Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default);
}
