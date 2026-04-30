using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Generates one-line and detailed symbol summaries, along with probable callees and embeddings.
/// </summary>
public sealed class SymbolSummarizer(
    IChatClient chatClient,
    Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator,
    SummaryCache cache,
    ModelTierSelector modelTierSelector,
    SummarizationPromptBuilder promptBuilder)
{
    private const string SummaryInstructions =
        "You summarize source code precisely and concisely. Follow the user's formatting requirements exactly.";

    /// <summary>
    /// Summarizes the provided chunks.
    /// </summary>
    /// <param name="chunks">The chunks to summarize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A map from chunk identifier to generated summary.</returns>
    public async Task<IReadOnlyDictionary<string, SymbolSummary>> SummarizeAsync(
        IReadOnlyList<Chunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return new Dictionary<string, SymbolSummary>(StringComparer.Ordinal);
        }

        IReadOnlyList<Chunk> orderedChunks = SummarizationOrder.Order(chunks);
        Dictionary<string, Chunk> chunksById = orderedChunks.ToDictionary(chunk => chunk.Id, StringComparer.Ordinal);
        Dictionary<string, List<Chunk>> chunksByQualifiedName = BuildNameMap(orderedChunks, static chunk => chunk.FullyQualifiedName);
        Dictionary<string, List<Chunk>> chunksBySimpleName = BuildNameMap(orderedChunks, static chunk => chunk.Name);
        HashSet<string> nonLeafChunkIds = BuildNonLeafChunkIds(orderedChunks, chunksById, chunksByQualifiedName, chunksBySimpleName);
        Dictionary<string, SymbolSummary> results = new(StringComparer.Ordinal);

        foreach (Chunk chunk in orderedChunks)
        {
            bool isLeaf = !nonLeafChunkIds.Contains(chunk.Id);
            ModelTierSelector.ModelTier detailedTier = modelTierSelector.SelectDetailedTier(chunk, isLeaf);
            string cacheKey = detailedTier.ToString();
            string contentHash = ComputeContentHash(chunk.Content);

            string oneLine;
            string detailed;
            IReadOnlyList<string> probableCallees;

            if (cache.TryGet(contentHash, cacheKey, out SummaryCacheEntry? cachedEntry))
            {
                oneLine = cachedEntry.OneLine;
                detailed = cachedEntry.Detailed;
                probableCallees = cachedEntry.ProbableCallees;
            }
            else
            {
                oneLine = await GetTextResponseAsync(
                    modelTierSelector.SelectOneLineModel(),
                    promptBuilder.BuildOneLinePrompt(chunk),
                    cancellationToken).ConfigureAwait(false);

                IReadOnlyList<string> parentSummaries = GetParentSummaries(
                    chunk,
                    chunksById,
                    chunksByQualifiedName,
                    chunksBySimpleName,
                    results);

                string detailedPrompt = parentSummaries.Count == 0
                    ? promptBuilder.BuildDetailedPrompt(chunk)
                    : promptBuilder.BuildDetailedForImplementationPrompt(chunk, parentSummaries);
                string detailedResponse = await GetTextResponseAsync(
                    modelTierSelector.SelectDetailedModel(chunk, isLeaf),
                    AppendProbableCalleesInstruction(detailedPrompt),
                    cancellationToken).ConfigureAwait(false);

                ParsedDetailedSummary parsed = ParseDetailedSummary(detailedResponse);
                detailed = parsed.Detailed;
                probableCallees = parsed.ProbableCallees;

                cache.Set(contentHash, cacheKey, new SummaryCacheEntry(oneLine, detailed, probableCallees));
            }

            ReadOnlyMemory<float> embedding = await embeddingGenerator.GenerateEmbeddingAsync(oneLine, cancellationToken).ConfigureAwait(false);
            results[chunk.Id] = new SymbolSummary(oneLine, detailed, probableCallees, embedding);
        }

        return results;
    }

    private async Task<string> GetTextResponseAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        ChatResponse response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions
            {
                ModelId = model,
                Instructions = SummaryInstructions,
            },
            cancellationToken).ConfigureAwait(false);

        string text = string.Concat(
            response.Messages
                .SelectMany(static message => message.Contents.OfType<TextContent>())
                .Select(static content => content.Text));

        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException($"Model '{model}' returned an empty summarization response.")
            : text.Trim();
    }

    private static string AppendProbableCalleesInstruction(string prompt) =>
        $"{prompt}{Environment.NewLine}{Environment.NewLine}At the end, append a section exactly named 'Probable callees:' followed by one bullet per likely method or function call. If none are likely, write 'Probable callees: (none)'.";

    private static string ComputeContentHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }

    private static Dictionary<string, List<Chunk>> BuildNameMap(IEnumerable<Chunk> chunks, Func<Chunk, string> keySelector)
    {
        Dictionary<string, List<Chunk>> map = new(StringComparer.Ordinal);
        foreach (Chunk chunk in chunks)
        {
            string key = keySelector(chunk);
            if (!map.TryGetValue(key, out List<Chunk>? bucket))
            {
                bucket = [];
                map[key] = bucket;
            }

            bucket.Add(chunk);
        }

        return map;
    }

    private static HashSet<string> BuildNonLeafChunkIds(
        IReadOnlyList<Chunk> chunks,
        IReadOnlyDictionary<string, Chunk> chunksById,
        IReadOnlyDictionary<string, List<Chunk>> chunksByQualifiedName,
        IReadOnlyDictionary<string, List<Chunk>> chunksBySimpleName)
    {
        HashSet<string> nonLeafChunkIds = new(StringComparer.Ordinal);

        foreach (Chunk chunk in chunks)
        {
            if (!string.IsNullOrWhiteSpace(chunk.ParentId) && chunksById.ContainsKey(chunk.ParentId))
            {
                nonLeafChunkIds.Add(chunk.ParentId);
            }

            foreach (string dependencyName in chunk.Inherits ?? [])
            {
                if (TryResolveDependency(chunk, dependencyName, chunksByQualifiedName, chunksBySimpleName, out Chunk? dependency)
                    && dependency is not null)
                {
                    nonLeafChunkIds.Add(dependency.Id);
                }
            }

            foreach (string dependencyName in chunk.Implements ?? [])
            {
                if (TryResolveDependency(chunk, dependencyName, chunksByQualifiedName, chunksBySimpleName, out Chunk? dependency)
                    && dependency is not null)
                {
                    nonLeafChunkIds.Add(dependency.Id);
                }
            }
        }

        return nonLeafChunkIds;
    }

    private static IReadOnlyList<string> GetParentSummaries(
        Chunk chunk,
        IReadOnlyDictionary<string, Chunk> chunksById,
        IReadOnlyDictionary<string, List<Chunk>> chunksByQualifiedName,
        IReadOnlyDictionary<string, List<Chunk>> chunksBySimpleName,
        IReadOnlyDictionary<string, SymbolSummary> summaries)
    {
        List<string> parentSummaries = [];

        if (!string.IsNullOrWhiteSpace(chunk.ParentId)
            && chunksById.TryGetValue(chunk.ParentId, out Chunk? parentChunk)
            && summaries.TryGetValue(parentChunk.Id, out SymbolSummary? parentSummary))
        {
            parentSummaries.Add(parentSummary.Detailed);
        }

        foreach (string dependencyName in chunk.Inherits ?? [])
        {
            AddResolvedSummary(chunk, dependencyName, chunksByQualifiedName, chunksBySimpleName, summaries, parentSummaries);
        }

        foreach (string dependencyName in chunk.Implements ?? [])
        {
            AddResolvedSummary(chunk, dependencyName, chunksByQualifiedName, chunksBySimpleName, summaries, parentSummaries);
        }

        return parentSummaries;
    }

    private static void AddResolvedSummary(
        Chunk chunk,
        string dependencyName,
        IReadOnlyDictionary<string, List<Chunk>> chunksByQualifiedName,
        IReadOnlyDictionary<string, List<Chunk>> chunksBySimpleName,
        IReadOnlyDictionary<string, SymbolSummary> summaries,
        ICollection<string> parentSummaries)
    {
        if (TryResolveDependency(chunk, dependencyName, chunksByQualifiedName, chunksBySimpleName, out Chunk? dependency)
            && dependency is not null
            && summaries.TryGetValue(dependency.Id, out SymbolSummary? summary)
            && !parentSummaries.Contains(summary.Detailed, StringComparer.Ordinal))
        {
            parentSummaries.Add(summary.Detailed);
        }
    }

    private static bool TryResolveDependency(
        Chunk current,
        string dependencyName,
        IReadOnlyDictionary<string, List<Chunk>> chunksByQualifiedName,
        IReadOnlyDictionary<string, List<Chunk>> chunksBySimpleName,
        out Chunk? dependency)
    {
        if (chunksByQualifiedName.TryGetValue(dependencyName, out List<Chunk>? exactQualifiedMatch))
        {
            dependency = exactQualifiedMatch[0];
            return true;
        }

        string? currentNamespace = GetNamespace(current.FullyQualifiedName);
        if (!string.IsNullOrWhiteSpace(currentNamespace)
            && chunksByQualifiedName.TryGetValue($"{currentNamespace}.{dependencyName}", out List<Chunk>? namespaceQualifiedMatch))
        {
            dependency = namespaceQualifiedMatch[0];
            return true;
        }

        if (!chunksBySimpleName.TryGetValue(dependencyName, out List<Chunk>? simpleMatches) || simpleMatches.Count == 0)
        {
            dependency = null;
            return false;
        }

        List<Chunk> samePathMatches = simpleMatches
            .Where(match => string.Equals(match.Path, current.Path, StringComparison.Ordinal))
            .OrderBy(static match => match.Path, StringComparer.Ordinal)
            .ThenBy(static match => match.Range.StartLine)
            .ThenBy(static match => match.Range.StartColumn)
            .ToList();

        dependency = samePathMatches.Count > 0
            ? samePathMatches[0]
            : simpleMatches
                .OrderBy(static match => match.Path, StringComparer.Ordinal)
                .ThenBy(static match => match.Range.StartLine)
                .ThenBy(static match => match.Range.StartColumn)
                .First();
        return true;
    }

    private static string? GetNamespace(string fullyQualifiedName)
    {
        int separatorIndex = fullyQualifiedName.LastIndexOf('.');
        return separatorIndex <= 0 ? null : fullyQualifiedName[..separatorIndex];
    }

    private static ParsedDetailedSummary ParseDetailedSummary(string response)
    {
        string normalized = response.ReplaceLineEndings("\n").Trim();
        string[] lines = normalized.Split('\n');
        List<string> detailLines = [];
        List<string> probableCallees = [];
        bool insideProbableCallees = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.Trim();

            if (!insideProbableCallees && TryGetProbableCalleeHeaderValue(trimmed, out string? headerValue))
            {
                insideProbableCallees = true;
                AddProbableCallees(headerValue, probableCallees);
                continue;
            }

            if (insideProbableCallees)
            {
                if (TryParseBullet(trimmed, out string? bulletValue))
                {
                    AddProbableCallees(bulletValue, probableCallees);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                detailLines.Add(line);
                insideProbableCallees = false;
                continue;
            }

            detailLines.Add(line);
        }

        return new ParsedDetailedSummary(
            string.Join("\n", detailLines).Trim(),
            probableCallees.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool TryGetProbableCalleeHeaderValue(string line, out string? value)
    {
        const string PrimaryHeader = "Probable callees:";
        const string AlternateHeader = "Probable calls:";

        if (line.StartsWith(PrimaryHeader, StringComparison.OrdinalIgnoreCase))
        {
            value = line[PrimaryHeader.Length..].Trim();
            return true;
        }

        if (line.StartsWith(AlternateHeader, StringComparison.OrdinalIgnoreCase))
        {
            value = line[AlternateHeader.Length..].Trim();
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseBullet(string line, out string? value)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            value = line[2..].Trim();
            return true;
        }

        value = null;
        return false;
    }

    private static void AddProbableCallees(string? rawValue, ICollection<string> probableCallees)
    {
        if (string.IsNullOrWhiteSpace(rawValue) || string.Equals(rawValue, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string value in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                probableCallees.Add(value);
            }
        }
    }

    private sealed record ParsedDetailedSummary(string Detailed, IReadOnlyList<string> ProbableCallees);
}
