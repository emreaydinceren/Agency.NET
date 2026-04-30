using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Selects which configured model tier should summarize a chunk.
/// </summary>
public sealed class ModelTierSelector(SummarizerOptions options)
{
    /// <summary>
    /// Represents the supported summarization model tiers.
    /// </summary>
    public enum ModelTier
    {
        /// <summary>
        /// Strongest tier for interfaces and abstracts.
        /// </summary>
        Strong,

        /// <summary>
        /// Standard tier for non-leaf concrete symbols.
        /// </summary>
        Standard,

        /// <summary>
        /// Cheap tier for leaf detailed summaries.
        /// </summary>
        Cheap,

        /// <summary>
        /// Cheapest tier for one-line summaries.
        /// </summary>
        Cheapest,
    }

    /// <summary>
    /// Selects the detailed-summary tier for a chunk.
    /// </summary>
    /// <param name="chunk">The chunk to summarize.</param>
    /// <param name="isLeaf">A value indicating whether the symbol is a leaf in the inheritance or implementation graph.</param>
    /// <returns>The chosen tier.</returns>
    public ModelTier SelectDetailedTier(Chunk chunk, bool isLeaf)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.SymbolKind == SymbolKind.Interface || IsAbstract(chunk))
        {
            return ModelTier.Strong;
        }

        return isLeaf ? ModelTier.Cheap : ModelTier.Standard;
    }

    /// <summary>
    /// Selects the configured model name for a detailed summary.
    /// </summary>
    /// <param name="chunk">The chunk to summarize.</param>
    /// <param name="isLeaf">A value indicating whether the symbol is a leaf in the inheritance or implementation graph.</param>
    /// <returns>The configured model name.</returns>
    public string SelectDetailedModel(Chunk chunk, bool isLeaf) => this.GetModelName(this.SelectDetailedTier(chunk, isLeaf));

    /// <summary>
    /// Selects the tier for a one-line summary.
    /// </summary>
    /// <returns>The cheapest tier.</returns>
    public ModelTier SelectOneLineTier() => ModelTier.Cheapest;

    /// <summary>
    /// Selects the configured model name for a one-line summary.
    /// </summary>
    /// <returns>The configured model name.</returns>
    public string SelectOneLineModel() => this.GetModelName(this.SelectOneLineTier());

    /// <summary>
    /// Resolves the configured model name for a tier.
    /// </summary>
    /// <param name="tier">The tier to resolve.</param>
    /// <returns>The configured model name.</returns>
    public string GetModelName(ModelTier tier) => tier switch
    {
        ModelTier.Strong => options.StrongModel,
        ModelTier.Standard => options.StandardModel,
        ModelTier.Cheap => options.CheapModel,
        ModelTier.Cheapest => options.CheapestModel,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    private static bool IsAbstract(Chunk chunk)
    {
        if (chunk.SymbolKind != SymbolKind.Class)
        {
            return false;
        }

        return ContainsAbstractKeyword(chunk.Signature) || ContainsAbstractKeyword(chunk.Content);
    }

    private static bool ContainsAbstractKeyword(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Contains("abstract", StringComparison.OrdinalIgnoreCase);
}
