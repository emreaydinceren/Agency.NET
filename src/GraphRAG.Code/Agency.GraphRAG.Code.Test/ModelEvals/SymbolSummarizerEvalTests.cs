using System.ClientModel;
using System.Globalization;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agency.GraphRAG.Code.Test.ModelEvals;

/// <summary>
/// Evaluation suite for <see cref="SymbolSummarizer"/> against the live LLM endpoint
/// declared in <c>src\GraphRAG.Code\Agency.GraphRAG.Code.Cli\appsettings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike unit tests, this suite measures summarization quality of the configured model
/// against a labeled dataset of symbol chunks and asserts minimum thresholds so prompt
/// regressions surface as test failures rather than silent quality drops.
/// </para>
/// <para>
/// Run with: <c>dotnet test --filter "Category=Eval"</c>. Requires LM Studio (or another
/// OpenAI-compatible endpoint) reachable at the URL configured in the CLI's
/// <c>appsettings.json</c>. Set env vars to override defaults:
/// <c>AGENCY_SYMBOL_SUMMARIZER_EVAL_THRESHOLD</c> (e.g. <c>0.60</c>) and
/// <c>AGENCY_SYMBOL_SUMMARIZER_EVAL_RUNS</c> (e.g. <c>5</c>).
/// </para>
/// <para>
/// The judge model is the same as the model under test, so absolute judge scores are
/// session-biased. Treat relative rankings across prompt variants as the primary signal.
/// </para>
/// </remarks>
[Trait("Category", "Eval")]
public sealed class SymbolSummarizerEvalTests(SymbolSummarizerEvalTests.LiveSummarizerFixture fixture, ITestOutputHelper output)
    : IClassFixture<SymbolSummarizerEvalTests.LiveSummarizerFixture>
{
    private const double DefaultCompositeScoreThreshold = 0.60;
    private const string ThresholdEnvironmentVariable = "AGENCY_SYMBOL_SUMMARIZER_EVAL_THRESHOLD";
    private const string RunCountEnvironmentVariable = "AGENCY_SYMBOL_SUMMARIZER_EVAL_RUNS";

    private readonly LiveSummarizerFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    // ── Test methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full labeled dataset through the live summarizer with production prompts and asserts
    /// the composite score meets the threshold.
    /// </summary>
    [Fact]
    public async Task SummarizeAsync_AgainstConfiguredModel_MeetsQualityThreshold()
    {
        if (!string.IsNullOrEmpty(this._fixture.SkipReason))
        {
            Assert.Skip(this._fixture.SkipReason);
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IReadOnlyList<EvalCase> dataset = EvalDataset.All;
        List<EvalResult> results = new(dataset.Count);
        foreach (EvalCase evalCase in dataset)
        {
            results.Add(await RunOneAsync(this._fixture.Summarizer, this._fixture.ChatClient, evalCase, cancellationToken));
        }

        EvalReport report = EvalReport.From(this._fixture.ModelId, results);
        this._output.WriteLine(report.Render());

        double threshold = EvalTestHelpers.ReadThreshold(ThresholdEnvironmentVariable, DefaultCompositeScoreThreshold);
        Assert.True(
            report.CompositeScore >= threshold,
            $"Composite {report.CompositeScore:P1} fell below threshold {threshold:P1} on {this._fixture.ModelId}. See output for per-case breakdown.");
    }

    /// <summary>
    /// Per-case visibility: each labeled chunk becomes its own test case so the test explorer
    /// surfaces which exact symbols the model summarizes poorly.
    /// </summary>
    [Theory]
    [MemberData(nameof(EvalCaseData))]
    public async Task SummarizeAsync_PerCase_MeetsExpectations(string label)
    {
        if (!string.IsNullOrEmpty(this._fixture.SkipReason))
        {
            Assert.Skip(this._fixture.SkipReason);
        }

        EvalCase evalCase = EvalDataset.All.First(c => c.Label == label);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EvalResult result = await RunOneAsync(this._fixture.Summarizer, this._fixture.ChatClient, evalCase, cancellationToken);

        Assert.True(
            result.OneLineKeywordRecall >= 0.5,
            $"OneLineKeywordRecall {result.OneLineKeywordRecall:P0} below 0.5 for \"{evalCase.Label}\". One-line: \"{result.PredictedOneLine}\".");
        Assert.True(
            result.DetailedKeywordRecall >= 0.5,
            $"DetailedKeywordRecall {result.DetailedKeywordRecall:P0} below 0.5 for \"{evalCase.Label}\". Detailed (first 200 chars): \"{result.PredictedDetailed[..Math.Min(200, result.PredictedDetailed.Length)]}\".");
    }

    /// <summary>Theory data for <see cref="SummarizeAsync_PerCase_MeetsExpectations"/>.</summary>
    public static TheoryData<string> EvalCaseData()
    {
        TheoryData<string> data = new();
        foreach (EvalCase evalCase in EvalDataset.All)
        {
            data.Add(evalCase.Label);
        }

        return data;
    }

    /// <summary>
    /// Sweeps a curated set of prompt variants against the dataset (multiple runs), prints a
    /// side-by-side composite-score comparison, persists a markdown report to TestResults/,
    /// and asserts the best variant clears the threshold.
    /// </summary>
    [Fact]
    public async Task SummarizeAsync_PromptVariantSweep_BestVariantBeatsThreshold()
    {
        if (!string.IsNullOrEmpty(this._fixture.SkipReason))
        {
            Assert.Skip(this._fixture.SkipReason);
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IReadOnlyList<EvalCase> dataset = EvalDataset.All;
        IReadOnlyList<PromptVariant> variants = PromptVariants.All;
        int runCount = EvalTestHelpers.ReadRunCount(RunCountEnvironmentVariable);

        List<VariantOutcome> outcomes = new(variants.Count);
        foreach (PromptVariant variant in variants)
        {
            List<EvalReport> reports = new(runCount);
            for (int run = 0; run < runCount; run++)
            {
                using SummaryCache sweepCache = new(":memory:");
                SymbolSummarizer summarizer = variant.IsBaseline
                    ? new SymbolSummarizer(
                        this._fixture.ChatClient,
                        sweepCache,
                        this._fixture.ModelTierSelector,
                        this._fixture.PromptBuilder,
                        this._fixture.SummarizerOptions,
                        NullLogger<SymbolSummarizer>.Instance)
                    : new SymbolSummarizer(
                        this._fixture.ChatClient,
                        sweepCache,
                        this._fixture.ModelTierSelector,
                        this._fixture.SummarizerOptions,
                        NullLogger<SymbolSummarizer>.Instance,
                        variant.OneLinePromptBuilder,
                        variant.DetailedPromptBuilder,
                        variant.OneLineInstructions,
                        variant.DetailedInstructions);

                List<EvalResult> results = new(dataset.Count);
                foreach (EvalCase evalCase in dataset)
                {
                    results.Add(await RunOneAsync(summarizer, this._fixture.ChatClient, evalCase, cancellationToken));
                }

                reports.Add(EvalReport.From(this._fixture.ModelId, results));
            }

            outcomes.Add(new VariantOutcome(variant, reports));
        }

        string report = VariantComparison.Render(this._fixture.ModelId, outcomes);
        this._output.WriteLine(report);

        string reportsDir = Path.Combine(EvalTestHelpers.FindRepoRoot(), "TestResults");
        Directory.CreateDirectory(reportsDir);
        string reportPath = Path.Combine(reportsDir, $"symbol-summarizer-sweep-{DateTime.UtcNow:yyyy-MM-dd_HHmmss}Z.md");
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);
        this._output.WriteLine($"Report written to: {reportPath}");

        VariantOutcome winner = outcomes.OrderByDescending(static o => o.MeanCompositeScore).First();
        double threshold = EvalTestHelpers.ReadThreshold(ThresholdEnvironmentVariable, DefaultCompositeScoreThreshold);
        Assert.True(
            winner.MeanCompositeScore >= threshold,
            $"Best prompt variant '{winner.Variant.Name}' achieved mean {winner.MeanCompositeScore:P1} across {winner.RunCount} run(s), below threshold {threshold:P1} on {this._fixture.ModelId}. See output for full comparison.");
    }

    // ── Eval helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a single <see cref="EvalCase"/> through the summarizer and returns a fully
    /// populated <see cref="EvalResult"/>.
    /// </summary>
    private static async Task<EvalResult> RunOneAsync(
        SymbolSummarizer summarizer,
        IChatClient judgeChatClient,
        EvalCase evalCase,
        CancellationToken cancellationToken)
    {
        SummarizationResult result = await summarizer.SummarizeAsync(evalCase.Chunks, cancellationToken).ConfigureAwait(false);

        result.Summaries.TryGetValue(evalCase.TargetChunkId, out SymbolSummary? symbolSummary);
        string oneLine = symbolSummary?.OneLine ?? string.Empty;
        string detailed = symbolSummary?.Detailed ?? string.Empty;
        IReadOnlyList<string> callees = symbolSummary?.ProbableCallees ?? [];

        int oneLineDenominator = Math.Max(evalCase.ExpectedOneLineKeywords.Count, 1);
        double oneLineKwRecall = evalCase.ExpectedOneLineKeywords.Count(
            kw => oneLine.Contains(kw, StringComparison.OrdinalIgnoreCase)) / (double)oneLineDenominator;

        int detailedDenominator = Math.Max(evalCase.ExpectedDetailedKeywords.Count, 1);
        double detailedKwRecall = evalCase.ExpectedDetailedKeywords.Count(
            kw => detailed.Contains(kw, StringComparison.OrdinalIgnoreCase)) / (double)detailedDenominator;

        int calleeDenominator = Math.Max(evalCase.ExpectedCallees.Count, 1);
        double calleeRecall = evalCase.ExpectedCallees.Count == 0
            ? 1.0
            : evalCase.ExpectedCallees.Count(
                expected => callees.Any(predicted => predicted.Contains(expected, StringComparison.OrdinalIgnoreCase)))
              / (double)calleeDenominator;

        bool isOneSentence = CountSentences(oneLine) == 1;

        double oneLineJudgeScore = 0.2;
        double detailedJudgeScore = 0.2;
        Exception? judgeError = null;
        try
        {
            oneLineJudgeScore = await JudgeSummaryAsync(judgeChatClient, evalCase.GoldOneLine, oneLine, cancellationToken).ConfigureAwait(false);
            detailedJudgeScore = await JudgeSummaryAsync(judgeChatClient, evalCase.GoldDetailed, detailed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            judgeError = ex;
        }

        return new EvalResult(
            Case: evalCase,
            PredictedOneLine: oneLine,
            PredictedDetailed: detailed,
            PredictedCallees: callees,
            OneLineKeywordRecall: oneLineKwRecall,
            OneLineJudgeScore: oneLineJudgeScore,
            OneLineIsOneSentence: isOneSentence,
            DetailedKeywordRecall: detailedKwRecall,
            DetailedJudgeScore: detailedJudgeScore,
            CalleeRecall: calleeRecall,
            Error: judgeError);
    }

    /// <summary>
    /// Asks the judge LLM to score the candidate summary against a gold reference on a 1-5 scale,
    /// then maps the result to the [0, 1] range. Returns <c>0.2</c> on parse failure.
    /// </summary>
    private static async Task<double> JudgeSummaryAsync(
        IChatClient judgeChatClient,
        string goldReference,
        string candidateSummary,
        CancellationToken cancellationToken)
    {
        string prompt =
            $"""
            Grade the candidate symbol summary against the gold reference on a 1-5 scale.
            Gold: {goldReference}
            Candidate: {candidateSummary}
            Return only: Score: N
            """;

        ChatResponse response = await judgeChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string text = string.Concat(
            response.Messages.SelectMany(static m => m.Contents.OfType<TextContent>())
                .Select(static c => c.Text));

        return ParseJudgeScore(text);
    }

    /// <summary>
    /// Parses a <c>Score: N</c> line from judge output into [0, 1]. Returns <c>0.2</c> on failure.
    /// </summary>
    private static double ParseJudgeScore(string text)
    {
        string? line = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static l => l.StartsWith("Score:", StringComparison.OrdinalIgnoreCase));

        if (line is null)
        {
            return 0.2;
        }

        string raw = line["Score:".Length..].Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return 0.2;
        }

        return Math.Clamp(parsed, 1.0, 5.0) / 5.0;
    }

    /// <summary>
    /// Returns the number of sentences in <paramref name="text"/> using a simple heuristic
    /// (splits on <c>.</c>, <c>!</c>, <c>?</c> that are followed by whitespace or end-of-string).
    /// </summary>
    private static int CountSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        string trimmed = text.Trim();
        int count = 0;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if ((c == '.' || c == '!' || c == '?') && (i == (trimmed.Length - 1) || char.IsWhiteSpace(trimmed[i + 1])))
            {
                count++;
            }
        }

        return Math.Max(count, 1);
    }

    // ── Eval dataset ─────────────────────────────────────────────────────────

    /// <summary>One labeled symbol case: input chunks plus expected output signals.</summary>
    internal sealed record EvalCase(
        string Label,
        IReadOnlyList<Chunk> Chunks,
        string TargetChunkId,
        IReadOnlyList<string> ExpectedOneLineKeywords,
        string GoldOneLine,
        IReadOnlyList<string> ExpectedDetailedKeywords,
        string GoldDetailed,
        IReadOnlyList<string> ExpectedCallees);

    /// <summary>Outcome of one <see cref="EvalCase"/> run through the summarizer.</summary>
    internal sealed record EvalResult(
        EvalCase Case,
        string PredictedOneLine,
        string PredictedDetailed,
        IReadOnlyList<string> PredictedCallees,
        double OneLineKeywordRecall,
        double OneLineJudgeScore,
        bool OneLineIsOneSentence,
        double DetailedKeywordRecall,
        double DetailedJudgeScore,
        double CalleeRecall,
        Exception? Error)
    {
        /// <summary>Gets the one-line keywords that were not found in the predicted one-line summary.</summary>
        public IReadOnlyList<string> MissedOneLineKeywords =>
            this.Case.ExpectedOneLineKeywords
                .Where(kw => !this.PredictedOneLine.Contains(kw, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>
    /// Curated labeled dataset of four symbol summarization cases drawn from actual symbols
    /// in this repository. Covers: interface (Strong tier), standalone leaf class (Cheap tier),
    /// implementation with parent context (parent summary injected), and a single method.
    /// </summary>
    internal static class EvalDataset
    {
        private const string InterfaceChunkId = "eval-icluster-summarizer";
        private const string ModelTierSelectorChunkId = "eval-model-tier-selector";
        private const string ClusterSummarizerChunkId = "eval-cluster-summarizer";
        private const string ParseCoherenceChunkId = "eval-parse-coherence";

        private static readonly Chunk InterfaceChunk = new(
            Id: InterfaceChunkId,
            Path: @"src\GraphRAG.Code\Agency.GraphRAG.Code\Cluster\ClusterSummarizer.cs",
            Language: Language.CSharp,
            Granularity: ChunkGranularity.Type,
            Name: "IClusterSummarizer",
            FullyQualifiedName: "Agency.GraphRAG.Code.Cluster.IClusterSummarizer",
            Signature: "public interface IClusterSummarizer",
            Content:
                """
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
                """,
            Range: new ChunkSourceRange(0, 0, 12, 0),
            SymbolKind: SymbolKind.Interface,
            ImportsInScope: []);

        private static readonly Chunk ModelTierSelectorChunk = new(
            Id: ModelTierSelectorChunkId,
            Path: @"src\GraphRAG.Code\Agency.GraphRAG.Code\Summarizer\ModelTierSelector.cs",
            Language: Language.CSharp,
            Granularity: ChunkGranularity.Type,
            Name: "ModelTierSelector",
            FullyQualifiedName: "Agency.GraphRAG.Code.Summarizer.ModelTierSelector",
            Signature: "public sealed class ModelTierSelector",
            Content:
                """
                /// <summary>
                /// Selects which configured model tier should summarize a chunk.
                /// </summary>
                public sealed class ModelTierSelector(IOptions<SummarizerOptions> options)
                {
                    /// <summary>Represents the supported summarization model tiers.</summary>
                    public enum ModelTier { Strong, Standard, Cheap, Cheapest }

                    /// <summary>Selects the detailed-summary tier for a chunk.</summary>
                    public ModelTier SelectDetailedTier(Chunk chunk, bool isLeaf)
                    {
                        ArgumentNullException.ThrowIfNull(chunk);
                        if (chunk.SymbolKind == SymbolKind.Interface || IsAbstract(chunk))
                            return ModelTier.Strong;
                        return isLeaf ? ModelTier.Cheap : ModelTier.Standard;
                    }

                    /// <summary>Selects the configured model name for a detailed summary.</summary>
                    public string SelectDetailedModel(Chunk chunk, bool isLeaf) =>
                        this.GetModelName(this.SelectDetailedTier(chunk, isLeaf));

                    /// <summary>Selects the tier for a one-line summary.</summary>
                    public ModelTier SelectOneLineTier() => ModelTier.Cheapest;

                    /// <summary>Selects the configured model name for a one-line summary.</summary>
                    public string SelectOneLineModel() => this.GetModelName(this.SelectOneLineTier());

                    /// <summary>Resolves the configured model name for a tier.</summary>
                    public string GetModelName(ModelTier tier) => tier switch
                    {
                        ModelTier.Strong => options.Value.StrongModel,
                        ModelTier.Standard => options.Value.StandardModel,
                        ModelTier.Cheap => options.Value.CheapModel,
                        ModelTier.Cheapest => options.Value.CheapestModel,
                        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
                    };

                    private static bool IsAbstract(Chunk chunk)
                    {
                        if (chunk.SymbolKind != SymbolKind.Class) return false;
                        return ContainsAbstractKeyword(chunk.Signature) || ContainsAbstractKeyword(chunk.Content);
                    }

                    private static bool ContainsAbstractKeyword(string? text) =>
                        !string.IsNullOrWhiteSpace(text) &&
                        text.Contains("abstract", StringComparison.OrdinalIgnoreCase);
                }
                """,
            Range: new ChunkSourceRange(0, 0, 50, 0),
            SymbolKind: SymbolKind.Class,
            ImportsInScope: [
                new ImportReference("Agency.GraphRAG.Code.Chunker", [], false),
                new ImportReference("Agency.GraphRAG.Code.Domain", [], false),
                new ImportReference("Microsoft.Extensions.Options", [], false),
            ]);

        private static readonly Chunk ClusterSummarizerChunk = new(
            Id: ClusterSummarizerChunkId,
            Path: @"src\GraphRAG.Code\Agency.GraphRAG.Code\Cluster\ClusterSummarizer.cs",
            Language: Language.CSharp,
            Granularity: ChunkGranularity.Type,
            Name: "ClusterSummarizer",
            FullyQualifiedName: "Agency.GraphRAG.Code.Cluster.ClusterSummarizer",
            Signature: "public sealed class ClusterSummarizer : IClusterSummarizer",
            Content:
                """
                /// <summary>Produces cluster summaries, classifications, and embeddings.</summary>
                public sealed class ClusterSummarizer : IClusterSummarizer
                {
                    private readonly IChatClient _chatClient;
                    private readonly Agency.Embeddings.Common.IEmbeddingGenerator _embeddingGenerator;
                    private readonly Func<ClusterSummaryRequest, string> _primaryPromptBuilder;
                    private readonly Func<ClusterSummaryRequest, string> _utilityPromptBuilder;

                    public ClusterSummarizer(IChatClient chatClient, Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator)
                        : this(chatClient, embeddingGenerator, BuildPrimaryPrompt, BuildUtilityPrompt) { }

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
                                response.Messages.SelectMany(static m => m.Contents.OfType<TextContent>())
                                    .Select(static c => c.Text));
                            string summary = ExtractValue(text, "Summary") ?? text.Trim();
                            ClusterType type = request.Origin == ClusterMembershipKind.Utility
                                ? ClusterType.Infrastructure
                                : ParseType(ExtractValue(text, "Type"));
                            double coherence = ParseCoherence(ExtractValue(text, "Coherence"));
                            ReadOnlyMemory<float> embedding =
                                await this._embeddingGenerator.GenerateEmbeddingAsync(summary, cancellationToken).ConfigureAwait(false);
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
                }
                """,
            Range: new ChunkSourceRange(0, 0, 70, 0),
            SymbolKind: SymbolKind.Class,
            ImportsInScope: [
                new ImportReference("Agency.GraphRAG.Code.Domain", [], false),
                new ImportReference("Microsoft.Extensions.AI", [], false),
            ],
            Implements: ["Agency.GraphRAG.Code.Cluster.IClusterSummarizer"]);

        private static readonly Chunk ParseCoherenceChunk = new(
            Id: ParseCoherenceChunkId,
            Path: @"src\GraphRAG.Code\Agency.GraphRAG.Code\Cluster\ClusterSummarizer.cs",
            Language: Language.CSharp,
            Granularity: ChunkGranularity.Member,
            Name: "ParseCoherence",
            FullyQualifiedName: "Agency.GraphRAG.Code.Cluster.ClusterSummarizer.ParseCoherence",
            Signature: "private static double ParseCoherence(string? coherenceValue)",
            Content:
                """
                private static double ParseCoherence(string? coherenceValue)
                {
                    if (!double.TryParse(coherenceValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        return 1d;
                    }

                    return parsed > 1d ? Math.Clamp(parsed / 5d, 0d, 1d) : Math.Clamp(parsed, 0d, 1d);
                }
                """,
            Range: new ChunkSourceRange(147, 0, 155, 0),
            SymbolKind: SymbolKind.Method,
            ImportsInScope: [
                new ImportReference("System.Globalization", [], false),
            ]);

        /// <summary>All four eval cases.</summary>
        public static readonly IReadOnlyList<EvalCase> All =
        [
            // ── 1. Standalone interface (Strong tier, no parent context) ───────────

            new EvalCase(
                Label: "StandaloneInterface",
                Chunks: [InterfaceChunk],
                TargetChunkId: InterfaceChunkId,
                ExpectedOneLineKeywords: ["summar", "cluster", "request"],
                GoldOneLine: "Defines the contract for LLM-based summarization of a batch of code clusters.",
                ExpectedDetailedKeywords: ["summar", "cluster", "request", "async"],
                GoldDetailed: "Interface for cluster summarization; exposes a single SummarizeAsync method that accepts a list of ClusterSummaryRequest and returns populated Cluster domain objects.",
                ExpectedCallees: []),

            // ── 2. Standalone leaf class (Cheap tier, no parent context) ──────────

            new EvalCase(
                Label: "StandaloneLeafClass",
                Chunks: [ModelTierSelectorChunk],
                TargetChunkId: ModelTierSelectorChunkId,
                ExpectedOneLineKeywords: ["tier", "model", "select"],
                GoldOneLine: "Selects the LLM model tier for each chunk based on its symbol kind and leaf status in the inheritance graph.",
                ExpectedDetailedKeywords: ["tier", "interface", "abstract", "leaf", "strong", "cheap"],
                GoldDetailed: "Routes each chunk to one of four model tiers (Strong, Standard, Cheap, Cheapest) using symbol kind and leaf status; Strong for interfaces/abstract classes, Standard for non-leaf concretes, Cheap for leaves, Cheapest for all one-line summaries.",
                ExpectedCallees: []),

            // ── 3. Implementation with parent context injected ────────────────────
            // Interface is processed first (topological order) and its detailed summary
            // is injected into ClusterSummarizer's prompt via BuildDetailedForImplementationPrompt.

            new EvalCase(
                Label: "ImplementationWithParent",
                Chunks: [InterfaceChunk, ClusterSummarizerChunk],
                TargetChunkId: ClusterSummarizerChunkId,
                ExpectedOneLineKeywords: ["summar", "cluster", "embed"],
                GoldOneLine: "Produces cluster summaries, type classifications, and coherence scores by calling the LLM and generating embeddings for each result.",
                ExpectedDetailedKeywords: ["chat", "embed", "prompt", "summar"],
                GoldDetailed: "Implements IClusterSummarizer by sending each cluster's symbols to the LLM via a prompt builder, parsing the response for Summary, Type, and Coherence fields, then generating an embedding for the summary text.",
                ExpectedCallees: ["GetResponseAsync", "GenerateEmbeddingAsync"]),

            // ── 4. Static method (Member granularity, leaf) ───────────────────────

            new EvalCase(
                Label: "StaticMethod",
                Chunks: [ParseCoherenceChunk],
                TargetChunkId: ParseCoherenceChunkId,
                ExpectedOneLineKeywords: ["parse", "coher"],
                GoldOneLine: "Parses a raw coherence string to a double and clamps the result to the [0, 1] range.",
                ExpectedDetailedKeywords: ["parse", "clamp", "double"],
                GoldDetailed: "Parses the coherence value using double.TryParse with InvariantCulture; returns 1.0 on failure. Values above 1 are treated as a 1-5 scale (divided by 5); values already in [0,1] are clamped directly.",
                ExpectedCallees: ["TryParse", "Clamp"]),
        ];
    }

    // ── Eval report ──────────────────────────────────────────────────────────

    /// <summary>Aggregated quality metrics over one run of the eval dataset.</summary>
    internal sealed class EvalReport
    {
        private readonly string _modelId;
        private readonly IReadOnlyList<EvalResult> _results;

        private EvalReport(string modelId, IReadOnlyList<EvalResult> results)
        {
            this._modelId = modelId;
            this._results = results;
        }

        /// <summary>Gets the total number of evaluated cases.</summary>
        public int CaseCount => this._results.Count;

        /// <summary>
        /// Gets mean one-line quality: <c>0.6 * KeywordRecall + 0.4 * JudgeScore</c>.
        /// </summary>
        public double OneLineQuality =>
            this._results.Count == 0
                ? 0.0
                : this._results.Average(static r => (0.6 * r.OneLineKeywordRecall) + (0.4 * r.OneLineJudgeScore));

        /// <summary>
        /// Gets mean detailed quality: <c>0.35 * KeywordRecall + 0.35 * JudgeScore + 0.30 * CalleeRecall</c>.
        /// </summary>
        public double DetailedQuality =>
            this._results.Count == 0
                ? 0.0
                : this._results.Average(static r => (0.35 * r.DetailedKeywordRecall) + (0.35 * r.DetailedJudgeScore) + (0.30 * r.CalleeRecall));

        /// <summary>Gets the composite score: <c>0.4 * OneLineQuality + 0.6 * DetailedQuality</c>.</summary>
        public double CompositeScore => (0.4 * this.OneLineQuality) + (0.6 * this.DetailedQuality);

        /// <summary>Gets the fraction of one-line summaries that are exactly one sentence.</summary>
        public double OneLineSentenceCompliance =>
            this._results.Count == 0
                ? 0.0
                : this._results.Average(static r => r.OneLineIsOneSentence ? 1.0 : 0.0);

        /// <summary>Gets the individual case results that make up this report.</summary>
        public IReadOnlyList<EvalResult> Results => this._results;

        /// <summary>Creates a new <see cref="EvalReport"/> for the given model and results.</summary>
        public static EvalReport From(string modelId, IReadOnlyList<EvalResult> results) =>
            new(modelId, results);

        /// <summary>Renders a human-readable multi-line summary of the eval run.</summary>
        public string Render()
        {
            StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"SymbolSummarizer eval against model: {this._modelId}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Cases: {this._results.Count}  Composite: {this.CompositeScore:P1}  OneLineQuality: {this.OneLineQuality:P1}  DetailedQuality: {this.DetailedQuality:P1}  SentenceCompliance: {this.OneLineSentenceCompliance:P0}");
            sb.AppendLine();
            sb.AppendLine("Per-case results:");

            const int labelWidth = 28;
            foreach (EvalResult r in this._results)
            {
                string label = r.Case.Label.PadRight(labelWidth);
                string sentence = r.OneLineIsOneSentence ? "1-sent" : "multi";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {label}  OneLine: KW={r.OneLineKeywordRecall:P0} Judge={r.OneLineJudgeScore:P0} [{sentence}]  Detailed: KW={r.DetailedKeywordRecall:P0} Judge={r.DetailedJudgeScore:P0} Callees={r.CalleeRecall:P0}");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    OneLine: {r.PredictedOneLine.Replace("\n", " ", StringComparison.Ordinal)[..Math.Min(100, r.PredictedOneLine.Length)]}");
            }

            IReadOnlyList<EvalResult> oneLineMisses = this._results
                .Where(static r => r.MissedOneLineKeywords.Count > 0)
                .ToList();

            if (oneLineMisses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("One-line keyword misses:");
                foreach (EvalResult miss in oneLineMisses)
                {
                    string missed = string.Join(", ", miss.MissedOneLineKeywords.Select(static kw => $"'{kw}'"));
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"  {miss.Case.Label.PadRight(28)}  missed: {missed}");
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    → \"{miss.PredictedOneLine[..Math.Min(120, miss.PredictedOneLine.Length)]}\"");
                }
            }

            IReadOnlyList<EvalResult> qualityMisses = this._results
                .Where(static r => r.OneLineKeywordRecall < 0.5 || r.DetailedKeywordRecall < 0.5)
                .ToList();

            if (qualityMisses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Quality misses (KeywordRecall < 0.5):");
                foreach (EvalResult miss in qualityMisses)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"  {miss.Case.Label}  OneLine KW: {miss.OneLineKeywordRecall:P0}  Detailed KW: {miss.DetailedKeywordRecall:P0}");
                }
            }

            return sb.ToString();
        }
    }

    // ── Prompt variant types ─────────────────────────────────────────────────

    /// <summary>One named prompt design: builders for both LLM calls plus system instructions.</summary>
    /// <param name="Name">Short name used as the row label in the comparison table.</param>
    /// <param name="Description">One-line description of what this variant is testing.</param>
    /// <param name="OneLinePromptBuilder">Builds the user message for the one-line call.</param>
    /// <param name="DetailedPromptBuilder">Builds the user message for the detailed call.</param>
    /// <param name="OneLineInstructions">System instructions for the one-line call.</param>
    /// <param name="DetailedInstructions">System instructions for the detailed call.</param>
    /// <param name="IsBaseline">When <see langword="true"/>, the sweep uses the production constructor so production prompts are exercised verbatim.</param>
    internal sealed record PromptVariant(
        string Name,
        string Description,
        Func<Chunk, string> OneLinePromptBuilder,
        Func<Chunk, IReadOnlyList<string>, string> DetailedPromptBuilder,
        string OneLineInstructions,
        string DetailedInstructions,
        bool IsBaseline);

    /// <summary>Aggregated outcome of running one <see cref="PromptVariant"/> across the dataset, possibly multiple times.</summary>
    internal sealed record VariantOutcome(PromptVariant Variant, IReadOnlyList<EvalReport> Reports)
    {
        /// <summary>Gets the number of runs performed.</summary>
        public int RunCount => this.Reports.Count;

        /// <summary>Gets the mean composite score across all runs.</summary>
        public double MeanCompositeScore =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.CompositeScore);

        /// <summary>Gets the sample standard deviation of composite scores across runs.</summary>
        public double CompositeScoreStdDev => EvalTestHelpers.SampleStdDev(this.Reports.Select(static r => r.CompositeScore));

        /// <summary>Gets the mean one-line quality across all runs.</summary>
        public double MeanOneLineQuality =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.OneLineQuality);

        /// <summary>Gets the mean detailed quality across all runs.</summary>
        public double MeanDetailedQuality =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.DetailedQuality);

        /// <summary>Gets the mean one-line keyword recall across all runs and cases.</summary>
        public double MeanOneLineKwRecall =>
            this.Reports.Count == 0 ? 0.0 :
            this.Reports.Average(report => report.Results.Average(r => r.OneLineKeywordRecall));

        /// <summary>Gets the mean detailed keyword recall across all runs and cases.</summary>
        public double MeanDetailedKwRecall =>
            this.Reports.Count == 0 ? 0.0 :
            this.Reports.Average(report => report.Results.Average(r => r.DetailedKeywordRecall));

        /// <summary>Gets the mean callee recall across all runs and cases.</summary>
        public double MeanCalleeRecall =>
            this.Reports.Count == 0 ? 0.0 :
            this.Reports.Average(report => report.Results.Average(r => r.CalleeRecall));

        /// <summary>Gets the mean one-line sentence compliance across all runs and cases.</summary>
        public double MeanSentenceCompliance =>
            this.Reports.Count == 0 ? 0.0 :
            this.Reports.Average(static r => r.OneLineSentenceCompliance);
    }

    // ── Prompt variants ──────────────────────────────────────────────────────

    /// <summary>
    /// Named prompt variants swept against the live model to compare summarization quality.
    /// Each variant probes a different hypothesis about prompting strategies for symbol summaries.
    /// </summary>
    internal static class PromptVariants
    {
        // Production instruction constants (mirrored from SymbolSummarizer for variant anchoring).
        private const string ProductionOneLineInstructions =
            "You summarize source code precisely and concisely. Follow the user's formatting requirements exactly. Output only what is asked. Do not explain your reasoning or add any preamble.";

        private const string ProductionDetailedInstructions =
            """
            You are a code analyzer. Write a 2-3 paragraph prose summary of the provided code:
            1. What it does (purpose and responsibilities).
            2. How it does it (key steps, data flow, important calls).
            3. Anything non-obvious: side effects, risks, or constraints. Omit if nothing notable.

            Be direct. Stop when done. Do not pad, speculate, or add a conclusion.
            """;

        private const string StructuredDetailedInstructions =
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

        /// <summary>All 3 prompt variants to sweep.</summary>
        public static readonly IReadOnlyList<PromptVariant> All =
        [
            // 1. Baseline — production prompts verbatim.
            //    One-line: kind-first instruction (promoted 2026-05-19: +3.5pp composite, ±1.0pp σ,
            //    perfect KW and callee recall in 7-run sweep vs plain instruction).
            //    Detailed: prose format (promoted 2026-05-18: +3.9pp composite, +14pp callee recall
            //    vs 4-section structured format in 7-run sweep).
            new PromptVariant(
                Name: "Baseline",
                Description: "Production prompts — kind-first one-line (promoted 2026-05-19) + prose detailed (promoted 2026-05-18).",
                OneLinePromptBuilder: BuildProductionOneLine,
                DetailedPromptBuilder: BuildProductionDetailed,
                OneLineInstructions: ProductionOneLineInstructions,
                DetailedInstructions: ProductionDetailedInstructions,
                IsBaseline: true),

            // 2. StructuredRollback — detailed-side canary. Old 4-section format.
            //    Watch 'Detailed Quality' and 'Callee Recall': Baseline − StructuredRollback
            //    varied from +3.9pp (2026-05-18) to ~0pp (2026-05-19 session), indicating
            //    session-level variance. Monitor for consistent divergence.
            new PromptVariant(
                Name: "StructuredRollback",
                Description: "Per-session canary — old 4-section structured detailed format. Monitors prose advantage stability across sessions.",
                OneLinePromptBuilder: BuildProductionOneLine,
                DetailedPromptBuilder: BuildProductionDetailed,
                OneLineInstructions: ProductionOneLineInstructions,
                DetailedInstructions: StructuredDetailedInstructions,
                IsBaseline: false),

            // 3. OneLineRollback — one-line-side canary. Pre-promotion plain instruction.
            //    Anchors the kind-first advantage: Baseline − OneLineRollback should be ≈ +3.5pp
            //    (as in the 2026-05-19 7-run sweep). If the gap collapses, the kind-first benefit
            //    was session-specific.
            new PromptVariant(
                Name: "OneLineRollback",
                Description: "Per-session canary — old plain one-line instruction ('Write exactly one sentence that states this symbol's primary purpose…'). Anchors the kind-first promotion: gap vs Baseline should be ≈ +3.5pp.",
                OneLinePromptBuilder: BuildPlainOneLine,
                DetailedPromptBuilder: BuildProductionDetailed,
                OneLineInstructions: ProductionOneLineInstructions,
                DetailedInstructions: ProductionDetailedInstructions,
                IsBaseline: false),
        ];

        // ── Shared prompt builder helpers ─────────────────────────────────────

        private static string BuildProductionOneLine(Chunk chunk) =>
            BuildBasePrompt(chunk, "Write exactly one sentence that opens with this symbol's kind (e.g. 'Interface that…', 'Class that…', 'Method that…') and states its primary purpose. Output only that sentence — no preamble, no explanation.");

        private static string BuildPlainOneLine(Chunk chunk) =>
            BuildBasePrompt(chunk, "Write exactly one sentence that states this symbol's primary purpose. Output only that sentence — no preamble, no explanation.");

        private static string BuildProductionDetailed(Chunk chunk, IReadOnlyList<string> parentSummaries)
        {
            if (parentSummaries.Count == 0)
            {
                return BuildBasePrompt(chunk, "Write a detailed summary that covers responsibilities, inputs, outputs, side effects, and important collaborators or calls. Respond directly — no preamble or explanation of your process.");
            }

            // Mirrors SummarizationPromptBuilder.BuildDetailedForImplementationPrompt.
            StringBuilder sb = new();
            sb.AppendLine("You are summarizing a source-code symbol.");
            sb.AppendLine("Write a detailed summary that explains how this implementation fulfills its parent contract. Cover responsibilities, inputs, outputs, side effects, and important collaborators or calls. Respond directly — no preamble or explanation of your process.");
            sb.AppendLine();
            AppendChunkMetadata(sb, chunk);
            sb.AppendLine();
            sb.AppendLine("Parent context:");
            foreach (string parentSummary in parentSummaries)
            {
                sb.Append("- ");
                sb.AppendLine(parentSummary.Length > 500 ? parentSummary[..500] : parentSummary);
            }

            sb.AppendLine();
            sb.AppendLine("Source:");
            sb.AppendLine("```");
            sb.AppendLine(chunk.Content);
            sb.Append("```");
            return sb.ToString();
        }

        private static string BuildBasePrompt(Chunk chunk, string instruction)
        {
            // Mirrors SummarizationPromptBuilder.CreateBasePrompt.
            StringBuilder sb = new();
            sb.AppendLine("You are summarizing a source-code symbol.");
            sb.AppendLine(instruction);
            sb.AppendLine();
            AppendChunkMetadata(sb, chunk);
            sb.AppendLine();
            sb.AppendLine("Source:");
            sb.AppendLine("```");
            sb.AppendLine(chunk.Content);
            sb.Append("```");
            return sb.ToString();
        }

        private static void AppendChunkMetadata(StringBuilder sb, Chunk chunk)
        {
            // Mirrors SummarizationPromptBuilder.AppendMetadata.
            sb.Append("Language: "); sb.AppendLine(chunk.Language.ToString());
            sb.Append("Path: "); sb.AppendLine(chunk.Path);
            sb.Append("Symbol kind: "); sb.AppendLine(chunk.SymbolKind.ToString());
            sb.Append("Name: "); sb.AppendLine(chunk.Name);
            sb.Append("Fully qualified name: "); sb.AppendLine(chunk.FullyQualifiedName);
            sb.Append("Signature: "); sb.AppendLine(chunk.Signature ?? "(none)");
            sb.Append("Inherits: "); sb.AppendLine(chunk.Inherits is { Count: > 0 } inh ? string.Join(", ", inh) : "(none)");
            sb.Append("Implements: "); sb.AppendLine(chunk.Implements is { Count: > 0 } impl ? string.Join(", ", impl) : "(none)");
            sb.Append("Imports in scope: "); sb.AppendLine(chunk.ImportsInScope.Count > 0 ? string.Join(", ", chunk.ImportsInScope.Select(static r => r.Source)) : "(none)");
        }
    }

    // ── Variant comparison rendering ─────────────────────────────────────────

    /// <summary>
    /// Renders a side-by-side comparison of multiple <see cref="VariantOutcome"/>s as a markdown
    /// table including composite scores, component metrics, and the winner delta vs baseline.
    /// </summary>
    internal static class VariantComparison
    {
        /// <summary>Renders the comparison report as a markdown string.</summary>
        public static string Render(string modelId, IReadOnlyList<VariantOutcome> outcomes)
        {
            int runCount = outcomes.Count == 0 ? 0 : outcomes[0].RunCount;
            int caseCount = outcomes.Count == 0 ? 0 : outcomes[0].Reports[0].CaseCount;
            bool multiRun = runCount > 1;

            StringBuilder sb = new();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# SymbolSummarizer prompt-variant sweep against model: {modelId}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Variants: {outcomes.Count}  Cases per variant: {caseCount}  Runs per variant: {runCount}");
            sb.AppendLine();

            string compositeHeader = multiRun ? "Composite (±σ pp)" : "Composite";
            sb.AppendLine($"| Variant | {compositeHeader} | 1-Line Quality | Detailed Quality | 1-Line KW | Detailed KW | Callee Recall | Sentence% |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

            foreach (VariantOutcome outcome in outcomes)
            {
                string compositeCell = multiRun
                    ? $"{outcome.MeanCompositeScore:P1} ±{(outcome.CompositeScoreStdDev * 100.0).ToString("F1", CultureInfo.InvariantCulture)}"
                    : outcome.MeanCompositeScore.ToString("P1", CultureInfo.InvariantCulture);

                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {outcome.Variant.Name} | {compositeCell} | {outcome.MeanOneLineQuality:P1} | {outcome.MeanDetailedQuality:P1} | {outcome.MeanOneLineKwRecall:F2} | {outcome.MeanDetailedKwRecall:F2} | {outcome.MeanCalleeRecall:F2} | {outcome.MeanSentenceCompliance:P0} |");
            }

            sb.AppendLine();
            sb.AppendLine("## Variant descriptions");
            sb.AppendLine();
            foreach (VariantOutcome outcome in outcomes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{outcome.Variant.Name}** — {outcome.Variant.Description}");
            }

            VariantOutcome winner = outcomes.OrderByDescending(static o => o.MeanCompositeScore).First();
            VariantOutcome? baseline = outcomes.FirstOrDefault(static o => o.Variant.IsBaseline);

            sb.AppendLine();
            sb.AppendLine("## Winner");
            sb.AppendLine();
            string winnerScore = multiRun
                ? $"{winner.MeanCompositeScore:P1} (±{(winner.CompositeScoreStdDev * 100.0).ToString("F1", CultureInfo.InvariantCulture)} pp σ over {winner.RunCount} runs)"
                : winner.MeanCompositeScore.ToString("P1", CultureInfo.InvariantCulture);

            if (baseline is not null && !winner.Variant.IsBaseline)
            {
                double deltaPoints = (winner.MeanCompositeScore - baseline.MeanCompositeScore) * 100.0;
                string sign = deltaPoints >= 0 ? "+" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Winner: **{winner.Variant.Name}** at {winnerScore} ({sign}{deltaPoints.ToString("F1", CultureInfo.InvariantCulture)} pp vs Baseline {baseline.MeanCompositeScore:P1}).");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Winner: **{winner.Variant.Name}** at {winnerScore}.");
            }

            return sb.ToString();
        }
    }

    // ── ModelIdDefaultingChatClient ──────────────────────────────────────────

    /// <summary>
    /// Wraps an inner <see cref="IChatClient"/> and injects a default <see cref="ChatOptions.ModelId"/>
    /// when the caller does not supply one. Required because <c>OpenAIClient.CreateChatClient()</c>
    /// returns a model-agnostic client and the judge calls do not specify a model.
    /// </summary>
    private sealed class ModelIdDefaultingChatClient(IChatClient inner, string modelId) : IChatClient
    {
        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatOptions effective = options ?? new ChatOptions();
            effective.ModelId ??= modelId;
            return inner.GetResponseAsync(messages, effective, cancellationToken);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatOptions effective = options ?? new ChatOptions();
            effective.ModelId ??= modelId;
            return inner.GetStreamingResponseAsync(messages, effective, cancellationToken);
        }

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            inner.GetService(serviceType, serviceKey);

        /// <inheritdoc />
        public void Dispose() => inner.Dispose();
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared fixture that locates the CLI's <c>appsettings.json</c>, builds an
    /// <see cref="IChatClient"/> against the configured LM Studio endpoint, and wires up the
    /// production <see cref="SymbolSummarizer"/> under test. If the endpoint is unreachable a
    /// human-readable <see cref="SkipReason"/> is set and individual tests call <c>Assert.Skip</c>.
    /// </summary>
    public sealed class LiveSummarizerFixture : IAsyncLifetime
    {
        private IChatClient? _chatClient;
        private SymbolSummarizer? _summarizer;
        private SummaryCache? _sharedCache;

        /// <summary>Gets the resolved model id (from <c>Summarizer:StrongModel</c>).</summary>
        public string ModelId { get; private set; } = "unknown";

        /// <summary>Gets the chat client (model-id defaulting wrapper) for direct use in sweep tests.</summary>
        public IChatClient ChatClient =>
            this._chatClient ?? throw new InvalidOperationException("ChatClient was not initialised; check SkipReason.");

        /// <summary>Gets the production summarizer under test.</summary>
        public SymbolSummarizer Summarizer =>
            this._summarizer ?? throw new InvalidOperationException("Summarizer was not initialised; check SkipReason.");

        /// <summary>Gets the configured model tier selector for constructing variant summarizers.</summary>
        public ModelTierSelector ModelTierSelector { get; private set; } = null!;

        /// <summary>Gets the configured prompt builder for constructing variant summarizers.</summary>
        public SummarizationPromptBuilder PromptBuilder { get; private set; } = null!;

        /// <summary>Gets the wrapped summarizer options for constructing variant summarizers.</summary>
        public IOptions<SummarizerOptions> SummarizerOptions { get; private set; } = null!;

        /// <summary>Gets a non-empty reason when the endpoint is unreachable, otherwise <see langword="null"/>.</summary>
        public string? SkipReason { get; private set; }

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            try
            {
                var config = EvalTestHelpers.LoadCliConfiguration();
                string baseUrl = EvalTestHelpers.RequireConfig(config, $"{EvalTestHelpers.LlmClientSection}:BaseUrl");
                string apiKey = EvalTestHelpers.RequireConfig(config, $"{EvalTestHelpers.LlmClientSection}:ApiKey");
                this.ModelId = EvalTestHelpers.RequireConfig(config, $"{EvalTestHelpers.SummarizerSection}:StrongModel");

                SummarizerOptions options = new()
                {
                    StrongModel = this.ModelId,
                    StandardModel = this.ModelId,
                    CheapModel = this.ModelId,
                    CheapestModel = this.ModelId,
                };
                this.SummarizerOptions = Options.Create(options);
                this.ModelTierSelector = new ModelTierSelector(this.SummarizerOptions);
                this.PromptBuilder = new SummarizationPromptBuilder(this.SummarizerOptions);

                LlmClientOptions llmOptions = new() { BaseUrl = baseUrl, ApiKey = apiKey };
                IChatClient rawClient = new OpenAIClient(Options.Create(llmOptions)).CreateChatClient();
                this._chatClient = new ModelIdDefaultingChatClient(rawClient, this.ModelId);

                this._sharedCache = new SummaryCache(":memory:");
                this._summarizer = new SymbolSummarizer(
                    this._chatClient,
                    this._sharedCache,
                    this.ModelTierSelector,
                    this.PromptBuilder,
                    this.SummarizerOptions,
                    NullLogger<SymbolSummarizer>.Instance);

                // Smoke-check: summarize a trivial chunk to verify the endpoint is reachable.
                using CancellationTokenSource probeCts = new(TimeSpan.FromSeconds(60));
                Chunk probeChunk = new(
                    Id: "smoke-probe",
                    Path: @"src\Probe.cs",
                    Language: Language.CSharp,
                    Granularity: ChunkGranularity.Type,
                    Name: "Probe",
                    FullyQualifiedName: "Agency.Probe",
                    Signature: "public sealed class Probe",
                    Content: "public sealed class Probe { }",
                    Range: new ChunkSourceRange(0, 0, 1, 0),
                    SymbolKind: SymbolKind.Class,
                    ImportsInScope: []);
                _ = await this._summarizer.SummarizeAsync([probeChunk], probeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or ClientResultException)
            {
                this.SkipReason = $"Live LLM endpoint not reachable for SymbolSummarizer eval ({ex.GetType().Name}: {ex.Message}). Start LM Studio with the configured model loaded, then re-run.";
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            this._chatClient?.Dispose();
            this._sharedCache?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
