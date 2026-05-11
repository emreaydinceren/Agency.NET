using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Generates one-line and detailed symbol summaries, along with probable callees and embeddings.
/// </summary>
public sealed class SymbolSummarizer(
    IChatClient chatClient,
    SummaryCache cache,
    ModelTierSelector modelTierSelector,
    SummarizationPromptBuilder promptBuilder,
    IOptions<SummarizerOptions> options,
    ILogger<SymbolSummarizer> logger)
{
    private readonly TimeSpan requestTimeout = TimeSpan.FromMinutes(Math.Max(1, options.Value.RequestTimeoutMinutes));
    private readonly int maxOutputTokens = options.Value.MaxOutputTokens;

    private const int MaxProbableCallees = 10;

    private const string SummaryInstructions =
"""
You are a code analyzer. Your job: read the code provided and produce a concise summary.

Output format:
1. **Purpose** (1 sentence): what this code does.
2. **Key components** (3-7 bullets): functions/classes/modules and their roles.
3. **Flow** (2-4 sentences): how data/control moves through it.
4. **Notable** (0-3 bullets): non-obvious behavior, side effects, or risks. Skip if nothing notable.

Rules:
- One pass. Do not re-analyze your own output.
- If a section has nothing to say, omit it. Do not pad.
- Describe what the code does, not what it could do or should do.
- No refactoring suggestions, no style critique, no "consider..." unless explicitly asked.
- If the code is unclear or truncated, state that once and summarize what's visible. Do not speculate about missing pieces.
- Stop when the four sections are done. Do not add a conclusion or recap.
""";
    //private const string SummaryInstructions_old =
    //    "You summarize source code precisely and concisely. Follow the user's formatting requirements exactly. Output only what is asked. Do not explain your reasoning or add any preamble. /no_think";


    private readonly string strongModel = options.Value.StrongModel;
    private readonly string standardModel = options.Value.StandardModel;
    private readonly string cheapModel = options.Value.CheapModel;
    private readonly string cheapestModel = options.Value.CheapestModel;

    /// <summary>
    /// Summarizes the provided chunks, retrying up to 10 times on transient failures.
    /// Failed chunks are returned in the result and do not cause an exception.
    /// </summary>
    /// <param name="chunks">The chunks to summarize.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onProgress">Optional callback for progress updates (processed, failed, total, symbolName).</param>
    /// <returns>A result containing successful summaries and identifiers of failed chunks.</returns>
    public async Task<SummarizationResult> SummarizeAsync(
        IReadOnlyList<Chunk> chunks,
        CancellationToken cancellationToken = default,
        Action<int, int, int, string>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return new SummarizationResult(new Dictionary<string, SymbolSummary>(StringComparer.Ordinal), []);
        }

        IReadOnlyList<Chunk> orderedChunks = SummarizationOrder.Order(chunks);
        Dictionary<string, Chunk> chunksById = orderedChunks.ToDictionary(chunk => chunk.Id, StringComparer.Ordinal);
        Dictionary<string, List<Chunk>> chunksByQualifiedName = BuildNameMap(orderedChunks, static chunk => chunk.FullyQualifiedName);
        Dictionary<string, List<Chunk>> chunksBySimpleName = BuildNameMap(orderedChunks, static chunk => chunk.Name);
        HashSet<string> nonLeafChunkIds = BuildNonLeafChunkIds(orderedChunks, chunksById, chunksByQualifiedName, chunksBySimpleName);
        Dictionary<string, SymbolSummary> results = new(StringComparer.Ordinal);

        int processed = 0;
        int failed = 0;
        List<string> failedChunkIds = [];

        foreach (Chunk chunk in orderedChunks)
        {
            if (chunk.Granularity == ChunkGranularity.Statement)
            {
                onProgress?.Invoke(processed, failed, orderedChunks.Count, chunk.FullyQualifiedName);
                processed++;
                continue;
            }

            bool isLeaf = !nonLeafChunkIds.Contains(chunk.Id);
            ModelTierSelector.ModelTier detailedTier = modelTierSelector.SelectDetailedTier(chunk, isLeaf);
            string cacheKey = detailedTier.ToString();
            string contentHash = ComputeContentHash(chunk.Content);

            onProgress?.Invoke(processed, failed, orderedChunks.Count, chunk.FullyQualifiedName);

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

                bool summaryFailed = oneLine.Contains("[Unable to generate summary]", StringComparison.Ordinal)
                    || detailed.Contains("[Unable to generate summary]", StringComparison.Ordinal);
                if (!summaryFailed)
                {
                    cache.Set(contentHash, cacheKey, new SummaryCacheEntry(oneLine, detailed, probableCallees));
                }
            }

            if (oneLine.Contains("[Unable to generate summary]", StringComparison.Ordinal))
            {
                failed++;
                failedChunkIds.Add(chunk.Id);
                logger.LogWarning(
                    "Summary generation failed for symbol '{Symbol}' in '{Path}' (chunk {ChunkId}).",
                    chunk.FullyQualifiedName, chunk.Path, chunk.Id);
            }

            results[chunk.Id] = new SymbolSummary(oneLine, detailed, probableCallees);
            processed++;
        }

        return new SummarizationResult(results, failedChunkIds);
    }

    private async Task<string> GetTextResponseAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        const int MaxRetries = 10;
        int attempt = 0;

        while (attempt < MaxRetries)
        {
            attempt++;

            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(requestTimeout);

                ChatResponse response = await chatClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, prompt)],
                    new ChatOptions
                    {
                        ModelId = model,
                        Instructions = SummaryInstructions,
                        MaxOutputTokens = this.maxOutputTokens,
                    },
                    cts.Token).ConfigureAwait(false);

                string text = string.Concat(
                    response.Messages
                        .SelectMany(static message => message.Contents.OfType<TextContent>())
                        .Select(static content => content.Text));

                if (!string.IsNullOrWhiteSpace(text))
                {
                    string trimmed = text.Trim();
                    if (!IsRepetitiveResponse(trimmed))
                    {
                        return trimmed;
                    }

                    logger.LogWarning("Attempt {Attempt}/{MaxRetries}: model '{Model}' returned a repetitive response. Retrying.", attempt, MaxRetries, model);
                }
                else
                {
                    logger.LogWarning("Attempt {Attempt}/{MaxRetries}: model '{Model}' returned empty text. Retrying.", attempt, MaxRetries, model);
                }

                if (attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} timed out calling model '{Model}'. Retrying.", attempt, MaxRetries, model);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Timeout calling model '{model}' after {requestTimeout.TotalSeconds:F0} seconds. " +
                    $"The LLM service may be unavailable or unresponsive. " +
                    $"Check that your LLM service is running and responding to requests.");
            }
            catch (ClientResultException ex) when (ex.Status == 400)
            {
                throw new InvalidOperationException(
                    $"Bad request calling model '{model}'. " +
                    "The configured model ID is likely not available or not loaded by the current LLM provider. " +
                    "Verify the model exists in the provider model list and update Summarizer model settings. " +
                    $"Provider error: {ex.Message}",
                    ex);
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed calling model '{Model}'. Retrying.", attempt, MaxRetries, model);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Exhausted {MaxRetries} retries calling model '{Model}'. Returning failure marker.", MaxRetries, model);
                return "[Unable to generate summary]";
            }
        }

        logger.LogError("Exhausted {MaxRetries} retries calling model '{Model}'. Returning failure marker.", MaxRetries, model);
        return "[Unable to generate summary]";
    }

    private static string AppendProbableCalleesInstruction(string prompt) =>
        $"{prompt}{Environment.NewLine}{Environment.NewLine}At the end, append a section exactly named 'Probable callees:' followed by up to 10 bullets (one per likely callee). If none are likely, write 'Probable callees: (none)'. Stop after the list.";

    private static bool IsRepetitiveResponse(string text)
    {
        string[] lines = text.Split('\n');
        return lines
            .Select(static l => l.Trim())
            .Where(static l => l.Length > 0)
            .GroupBy(static l => l, StringComparer.Ordinal)
            .Any(static g => g.Count() >= 5);
    }

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
            probableCallees.Distinct(StringComparer.Ordinal).Take(MaxProbableCallees).ToArray());
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
