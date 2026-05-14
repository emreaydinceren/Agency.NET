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
public sealed class ClusterSummarizer : IClusterSummarizer
{
    private readonly IChatClient _chatClient;
    private readonly Agency.Embeddings.Common.IEmbeddingGenerator _embeddingGenerator;
    private readonly Func<ClusterSummaryRequest, string> _primaryPromptBuilder;
    private readonly Func<ClusterSummaryRequest, string> _utilityPromptBuilder;

    /// <summary>
    /// Initializes a new instance of <see cref="ClusterSummarizer"/> with the specified chat client and
    /// embedding generator; uses the production prompt builders.
    /// </summary>
    /// <param name="chatClient">The chat client used to generate cluster summaries.</param>
    /// <param name="embeddingGenerator">The embedding generator used to embed cluster summaries.</param>
    public ClusterSummarizer(IChatClient chatClient, Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator)
        : this(chatClient, embeddingGenerator, BuildPrimaryPrompt, BuildUtilityPrompt)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ClusterSummarizer"/> with the specified chat client,
    /// embedding generator, and custom prompt builders; allows callers to supply custom prompt builders
    /// (used by the evaluation suite).
    /// </summary>
    /// <param name="chatClient">The chat client used to generate cluster summaries.</param>
    /// <param name="embeddingGenerator">The embedding generator used to embed cluster summaries.</param>
    /// <param name="primaryPromptBuilder">The prompt builder for primary (non-utility) clusters.</param>
    /// <param name="utilityPromptBuilder">The prompt builder for utility clusters.</param>
    public ClusterSummarizer(
        IChatClient chatClient,
        Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator,
        Func<ClusterSummaryRequest, string> primaryPromptBuilder,
        Func<ClusterSummaryRequest, string> utilityPromptBuilder)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentNullException.ThrowIfNull(primaryPromptBuilder);
        ArgumentNullException.ThrowIfNull(utilityPromptBuilder);
        this._chatClient = chatClient;
        this._embeddingGenerator = embeddingGenerator;
        this._primaryPromptBuilder = primaryPromptBuilder;
        this._utilityPromptBuilder = utilityPromptBuilder;
    }

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
            string prompt = request.Origin == ClusterMembershipKind.Utility
                ? this._utilityPromptBuilder(request)
                : this._primaryPromptBuilder(request);
            ChatResponse response = await this._chatClient.GetResponseAsync(
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
            ReadOnlyMemory<float> embedding = await this._embeddingGenerator.GenerateEmbeddingAsync(summary, cancellationToken).ConfigureAwait(false);

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

    private static string BuildPrimaryPrompt(ClusterSummaryRequest request)
    {
        string joinedSymbols = string.Join(", ", request.Symbols.Select(static symbol => symbol.FullyQualifiedName ?? symbol.Name).OrderBy(static value => value, StringComparer.Ordinal));
        return $"Decision procedure:\n" +
               $"1. Read the symbol names and identify their unifying concept.\n" +
               $"2. If the concept is a domain operation, choose business. If it's plumbing (logging, retries, persistence), choose infrastructure. If it spans both, choose mixed.\n" +
               $"3. Tiebreaker: only choose mixed if the symbols genuinely belong to two unrelated domains. Multiple implementations of the same role (e.g. SqliteFoo + PostgresFoo, ClaudeClient + OpenAIClient) are infrastructure, not mixed. Do NOT choose mixed merely because a cluster contains many classes or spans several namespaces.\n" +
               $"4. Score coherence 1-5 by how tightly the symbols belong together.\n" +
               $"Now produce:\n" +
               $"Label: {request.Label}\nSymbols: {joinedSymbols}\nReturn:\nSummary: ...\nType: business|infrastructure|mixed\nCoherence: 1-5";
    }

    private static string BuildUtilityPrompt(ClusterSummaryRequest request)
    {
        string joinedSymbols = string.Join(", ", request.Symbols.Select(static symbol => symbol.FullyQualifiedName ?? symbol.Name).OrderBy(static value => value, StringComparer.Ordinal));
        return $"This cluster contains cross-cutting code used across the codebase. Describe its role, not a unifying business topic.\nLabel: {request.Label}\nSymbols: {joinedSymbols}\nReturn:\nSummary: ...\nCoherence: 1-5";
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
