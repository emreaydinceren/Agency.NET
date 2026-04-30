using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.References;

/// <summary>
/// Scores candidate symbol resolutions from direct name matches and optional LLM-extracted targets.
/// </summary>
public sealed class ReferenceScorer(ExternalPackageHeuristic externalPackageHeuristic)
{
    /// <summary>
    /// Scores the supplied candidates according to the reference-resolution signal taxonomy.
    /// </summary>
    /// <param name="identifier">The unresolved identifier as it appeared in source.</param>
    /// <param name="candidateSymbols">The candidate local symbols matched by name.</param>
    /// <param name="externalPackages">The declared external packages available in the current project scope.</param>
    /// <param name="llmExtractedTarget">The optional fully-qualified target inferred by an LLM.</param>
    /// <returns>The scored resolution results.</returns>
    /// <remarks>
    /// Confidence formula:
    /// name-match only = 0.60 for a single candidate, 0.35 per candidate when ambiguous;
    /// name-match + llm-extraction = 0.90;
    /// llm-extraction + external package match = 0.75;
    /// llm-extraction only without a traceable package = 0.20.
    /// </remarks>
    public IReadOnlyList<ResolutionResult> Score(
        string identifier,
        IReadOnlyList<Symbol> candidateSymbols,
        IReadOnlyList<ExternalPackage> externalPackages,
        string? llmExtractedTarget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(candidateSymbols);
        ArgumentNullException.ThrowIfNull(externalPackages);

        bool hasLlmTarget = !string.IsNullOrWhiteSpace(llmExtractedTarget);
        if (candidateSymbols.Count > 0)
        {
            double confidence = candidateSymbols.Count == 1 ? 0.60d : 0.35d;
            List<Signal> signals = [Signal.NameMatch];

            if (hasLlmTarget)
            {
                confidence = 0.90d;
                signals.Add(Signal.LlmExtraction);

                Symbol? bestMatch = candidateSymbols.FirstOrDefault(symbol =>
                    string.Equals(symbol.FullyQualifiedName, llmExtractedTarget, StringComparison.Ordinal)
                    || string.Equals(symbol.Name, llmExtractedTarget, StringComparison.Ordinal));
                if (bestMatch is not null)
                {
                    return [new ResolutionResult(bestMatch.Id, confidence, signals)];
                }
            }

            return candidateSymbols
                .Select(symbol => new ResolutionResult(symbol.Id, confidence, signals))
                .ToArray();
        }

        if (hasLlmTarget)
        {
            string? matchedPackage = externalPackageHeuristic.MatchPackage(llmExtractedTarget!, externalPackages);
            if (!string.IsNullOrWhiteSpace(matchedPackage))
            {
                return
                [
                    new ResolutionResult(
                        TargetSymbolId: null,
                        Confidence: 0.75d,
                        Signals: [Signal.LlmExtraction, Signal.ExternalLikely],
                        ExternalPackageName: matchedPackage),
                ];
            }

            return
            [
                new ResolutionResult(
                    TargetSymbolId: null,
                    Confidence: 0.20d,
                    Signals: [Signal.LlmExtraction, Signal.Unresolved]),
            ];
        }

        return [];
    }
}
