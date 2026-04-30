using System.Globalization;
using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Domain;
using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Summarizes clusters and classifies their role.
/// </summary>
public interface IClusterSummarizer
{
    /// <summary>
    /// Summarizes the supplied clusters.
    /// </summary>
    Task<IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster>> SummarizeAsync(
        IReadOnlyList<ClusterSummaryRequest> requests,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces cluster summaries, classifications, and embeddings.
/// </summary>
public sealed class ClusterSummarizer(IChatClient chatClient, Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator) : IClusterSummarizer
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster>> SummarizeAsync(
        IReadOnlyList<ClusterSummaryRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        List<Agency.GraphRAG.Code.Domain.Cluster> clusters = [];
        foreach (ClusterSummaryRequest request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prompt = BuildPrompt(request);
            ChatResponse response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string text = string.Concat(
                response.Messages.SelectMany(static message => message.Contents.OfType<TextContent>())
                    .Select(static content => content.Text));

            string summary = ExtractValue(text, "Summary") ?? text.Trim();
            ClusterType type = request.Origin == ClusterMembershipKind.Utility
                ? ClusterType.Infrastructure
                : ParseType(ExtractValue(text, "Type"));
            double coherence = ParseCoherence(ExtractValue(text, "Coherence"));
            ReadOnlyMemory<float> embedding = await embeddingGenerator.GenerateEmbeddingAsync(summary, cancellationToken).ConfigureAwait(false);

            clusters.Add(new Agency.GraphRAG.Code.Domain.Cluster
            {
                Id = request.ClusterId,
                Label = request.Label,
                Summary = summary,
                Type = type,
                CoherenceScore = coherence,
                Embedding = embedding.ToArray(),
            });
        }

        return clusters;
    }

    private static string BuildPrompt(ClusterSummaryRequest request)
    {
        string joinedSymbols = string.Join(", ", request.Symbols.Select(static symbol => symbol.FullyQualifiedName ?? symbol.Name).OrderBy(static value => value, StringComparer.Ordinal));
        return request.Origin == ClusterMembershipKind.Utility
            ? $"This cluster contains cross-cutting code used across the codebase. Describe its role, not a unifying business topic.\nLabel: {request.Label}\nSymbols: {joinedSymbols}\nReturn:\nSummary: ...\nCoherence: 1-5"
            : $"Identify the business concept this cluster owns. Describe the domain operation it implements.\nLabel: {request.Label}\nSymbols: {joinedSymbols}\nReturn:\nSummary: ...\nType: business|infrastructure|mixed\nCoherence: 1-5";
    }

    private static string? ExtractValue(string text, string key)
    {
        string prefix = key + ":";
        string? line = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return line is null ? null : line[prefix.Length..].Trim();
    }

    private static ClusterType ParseType(string? typeValue) =>
        typeValue?.Trim().ToLowerInvariant() switch
        {
            "business" => ClusterType.Business,
            "infrastructure" => ClusterType.Infrastructure,
            "mixed" => ClusterType.Mixed,
            _ => ClusterType.Mixed,
        };

    private static double ParseCoherence(string? coherenceValue)
    {
        if (!double.TryParse(coherenceValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return 1d;
        }

        return parsed > 1d ? Math.Clamp(parsed / 5d, 0d, 1d) : Math.Clamp(parsed, 0d, 1d);
    }
}

/// <summary>
/// Describes a cluster that requires summarization.
/// </summary>
/// <param name="ClusterId">The cluster identifier.</param>
/// <param name="Label">The human-readable cluster label.</param>
/// <param name="Origin">The origin of the cluster.</param>
/// <param name="Symbols">The member symbols.</param>
public sealed record ClusterSummaryRequest(
    Guid ClusterId,
    string Label,
    ClusterMembershipKind Origin,
    IReadOnlyList<Symbol> Symbols);
