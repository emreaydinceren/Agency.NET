using System.Text;

namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Turns retrieved clusters, symbols, and raw code into a compact prompt context.
/// </summary>
public sealed class ContextAssembler
{
    /// <summary>
    /// Assembles a query context within the supplied token budget.
    /// </summary>
    public QueryContextAssembly Assemble(QueryPlan plan, QueryRetrievalResult retrieval, int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(retrieval);

        if (tokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget));
        }

        List<string> sections = [];
        bool truncated = false;

        truncated |= TryAddSection(
            sections,
            "Cluster summaries",
            retrieval.Clusters
                .DistinctBy(static cluster => cluster.Cluster.Id)
                .OrderByDescending(static cluster => cluster.Score)
                .Select(static cluster => $"- {cluster.Cluster.Label} [{cluster.Cluster.Type}] {cluster.Cluster.Summary ?? "(no summary)"}"),
            tokenBudget);

        truncated |= TryAddSection(
            sections,
            "Relevant symbols",
            retrieval.Symbols
                .DistinctBy(static symbol => symbol.Symbol.Id)
                .OrderBy(static symbol => symbol.Depth)
                .ThenBy(static symbol => symbol.Symbol.FileId)
                .ThenBy(static symbol => symbol.Symbol.SourceRangeStart)
                .Select(static symbol => $"- {symbol.Symbol.FullyQualifiedName ?? symbol.Symbol.Name}: {symbol.Symbol.OneLineSummary ?? symbol.Symbol.Summary ?? symbol.Symbol.Signature ?? "(no summary)"}"),
            tokenBudget);

        truncated |= TryAddSection(
            sections,
            "Raw code",
            retrieval.Symbols
                .Where(static symbol => !string.IsNullOrWhiteSpace(symbol.RawCode))
                .DistinctBy(static symbol => symbol.Symbol.Id)
                .OrderBy(static symbol => symbol.Symbol.FileId)
                .ThenBy(static symbol => symbol.Symbol.SourceRangeStart)
                .Select(static symbol => $"## {symbol.Symbol.Name}{Environment.NewLine}```text{Environment.NewLine}{symbol.RawCode!.Trim()}{Environment.NewLine}```"),
            tokenBudget);

        if (retrieval.InfrastructureClusters.Count > 0)
        {
            truncated |= TryAddSection(
                sections,
                "Infrastructure footer",
                [
                    "Additional infrastructure clusters: " + string.Join(
                        ", ",
                        retrieval.InfrastructureClusters
                            .DistinctBy(static cluster => cluster.Cluster.Id)
                            .Select(static cluster => cluster.Cluster.Label)),
                ],
                tokenBudget);
        }

        if (retrieval.HasLowConfidenceReferences)
        {
            truncated |= TryAddSection(
                sections,
                "Confidence notes",
                ["Some reference links came from low-confidence evidence and may be incomplete."],
                tokenBudget);
        }

        string text = string.Join(Environment.NewLine + Environment.NewLine, sections);
        return new QueryContextAssembly
        {
            ContextText = text,
            EstimatedTokens = EstimateTokens(text),
            IsTruncated = truncated,
        };
    }

    private static bool TryAddSection(ICollection<string> sections, string title, IEnumerable<string> entries, int tokenBudget)
    {
        List<string> acceptedEntries = [];
        bool truncated = false;

        foreach (string entry in entries)
        {
            string candidateSection = $"{title}:{Environment.NewLine}{string.Join(Environment.NewLine, acceptedEntries.Append(entry))}";
            string candidateText = sections.Count == 0
                ? candidateSection
                : string.Join(Environment.NewLine + Environment.NewLine, sections.Append(candidateSection));
            if (EstimateTokens(candidateText) > tokenBudget)
            {
                truncated = true;
                break;
            }

            acceptedEntries.Add(entry);
        }

        if (acceptedEntries.Count > 0)
        {
            sections.Add($"{title}:{Environment.NewLine}{string.Join(Environment.NewLine, acceptedEntries)}");
        }

        return truncated;
    }

    private static int EstimateTokens(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
