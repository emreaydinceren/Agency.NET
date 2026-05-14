using System.ClientModel;
using System.Globalization;
using System.Text;
using Agency.GraphRAG.Code.Cluster;
using Agency.GraphRAG.Code.Domain;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;

namespace Agency.GraphRAG.Code.Test.ModelEvals;

/// <summary>
/// Evaluation suite for <see cref="ClusterSummarizer"/> against the live LLM endpoint
/// declared in <c>src\GraphRAG.Code\Agency.GraphRAG.Code.Cli\appsettings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the unit tests in <c>ClusterSummarizerTests</c>, this suite measures the
/// summarization quality of the configured model against a labeled dataset of clusters.
/// It reports composite quality scores and asserts a minimum threshold so a regression
/// in the deployed model surfaces as a test failure rather than a silent quality drop.
/// </para>
/// <para>
/// Run with: <c>dotnet test --filter "Category=Eval"</c>. Requires LM Studio
/// (or another OpenAI-compatible endpoint) reachable at the URL configured in the CLI's
/// <c>appsettings.json</c>. Set the env var <c>AGENCY_SUMMARIZER_EVAL_THRESHOLD</c>
/// (e.g. <c>0.65</c>) to override the default pass threshold. The <c>Eval</c> trait
/// (distinct from <c>Functional</c>) keeps this slow, model-dependent suite out of both
/// the default <c>Category!=Functional</c> run and the broader <c>Category=Functional</c>
/// sweep so it only executes when explicitly requested.
/// </para>
/// <para>
/// The judge model is the same as the model under test, so absolute judge scores are
/// biased. Treat relative rankings across prompt variants as more meaningful than
/// absolute numeric scores.
/// </para>
/// </remarks>
[Trait("Category", "Eval")]
public sealed class ClusterSummarizerEvalTests(ClusterSummarizerEvalTests.LiveSummarizerFixture fixture, ITestOutputHelper output)
    : IClassFixture<ClusterSummarizerEvalTests.LiveSummarizerFixture>
{
    private const double DefaultCompositeScoreThreshold = 0.65;
    private const string ThresholdEnvironmentVariable = "AGENCY_SUMMARIZER_EVAL_THRESHOLD";
    private const string RunCountEnvironmentVariable = "AGENCY_SUMMARIZER_EVAL_RUNS";

    private readonly LiveSummarizerFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    // ── Test methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full labeled dataset through the live summarizer with production prompts and asserts
    /// the composite score meets the threshold. Prints a per-case report via <see cref="ITestOutputHelper"/>.
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
            results.Add(await RunOneAsync(
                this._fixture.Summarizer,
                this._fixture.ChatClient,
                evalCase,
                cancellationToken));
        }

        EvalReport report = EvalReport.From(this._fixture.ModelId, results);
        this._output.WriteLine(report.Render());

        double threshold = EvalTestHelpers.ReadThreshold(ThresholdEnvironmentVariable, DefaultCompositeScoreThreshold);
        Assert.True(
            report.CompositeScore >= threshold,
            $"Composite {report.CompositeScore:P1} fell below threshold {threshold:P1} on {this._fixture.ModelId}. See output for per-case breakdown.");
    }

    /// <summary>
    /// Per-case visibility: each labeled cluster becomes its own test case so the test explorer surfaces
    /// which exact clusters the model summarizes poorly.
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
        EvalResult result = await RunOneAsync(
            this._fixture.Summarizer,
            this._fixture.ChatClient,
            evalCase,
            cancellationToken);

        Assert.True(
            result.KeywordRecall >= 0.5,
            $"KeywordRecall {result.KeywordRecall:P0} below 0.5 for \"{evalCase.Label}\". Summary: \"{result.PredictedSummary}\". Expected keywords: [{string.Join(", ", evalCase.ExpectedKeywords)}].");
        Assert.True(
            result.IsCorrectType,
            $"Type mismatch for \"{evalCase.Label}\": expected {evalCase.ExpectedType}, got {result.PredictedType?.ToString() ?? "<null>"}. Summary: \"{result.PredictedSummary}\".");
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
    /// Sweeps a curated set of prompt variants against the dataset (multiple runs) and the live model,
    /// prints a side-by-side composite-score comparison, persists a markdown report to TestResults/,
    /// and asserts the BEST variant clears the threshold.
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
            ClusterSummarizer summarizer = variant.IsBaseline
                ? new ClusterSummarizer(this._fixture.ChatClient, this._fixture.EmbeddingGenerator)
                : new ClusterSummarizer(this._fixture.ChatClient, this._fixture.EmbeddingGenerator, variant.PrimaryBuilder, variant.UtilityBuilder);

            List<EvalReport> reports = new(runCount);
            for (int run = 0; run < runCount; run++)
            {
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
        string reportPath = Path.Combine(reportsDir, $"summarizer-sweep-{DateTime.UtcNow:yyyy-MM-dd_HHmmss}Z.md");
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);
        this._output.WriteLine($"Report written to: {reportPath}");

        VariantOutcome winner = outcomes.OrderByDescending(static o => o.MeanCompositeScore).First();
        double threshold = EvalTestHelpers.ReadThreshold(ThresholdEnvironmentVariable, DefaultCompositeScoreThreshold);
        Assert.True(
            winner.MeanCompositeScore >= threshold,
            $"Best prompt variant '{winner.Variant.Name}' achieved mean {winner.MeanCompositeScore:P1} across {winner.RunCount} run(s), below threshold {threshold:P1} on {this._fixture.ModelId}. See output for full comparison.");
    }

    // ── Symbol factory ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal <see cref="Symbol"/> whose <see cref="Symbol.FullyQualifiedName"/>
    /// matches <paramref name="fqn"/>. All required fields are set to benign defaults.
    /// </summary>
    /// <param name="fqn">The fully qualified name of the symbol.</param>
    private static Symbol MakeSymbol(string fqn) =>
        new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Name = fqn.Split('.').Last(),
            FullyQualifiedName = fqn,
            Kind = SymbolKind.Class,
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 2,
        };

    // ── Eval helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a single <see cref="EvalCase"/> through the summarizer and returns a fully
    /// populated <see cref="EvalResult"/>.
    /// </summary>
    /// <param name="summarizer">The cluster summarizer under test.</param>
    /// <param name="judgeChatClient">Chat client used for LLM-as-judge scoring.</param>
    /// <param name="evalCase">The labeled input to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task<EvalResult> RunOneAsync(
        ClusterSummarizer summarizer,
        IChatClient judgeChatClient,
        EvalCase evalCase,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Symbol> symbols = evalCase.SymbolFqns.Select(MakeSymbol).ToList();
        ClusterSummaryRequest request = new(Guid.NewGuid(), evalCase.Label, evalCase.Origin, symbols);

        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters =
            await summarizer.SummarizeAsync([request], cancellationToken).ConfigureAwait(false);

        Agency.GraphRAG.Code.Domain.Cluster cluster = clusters[0];
        string summary = cluster.Summary ?? string.Empty;

        int denominator = Math.Max(evalCase.ExpectedKeywords.Count, 1);
        double keywordRecall = evalCase.ExpectedKeywords.Count(
            kw => summary.Contains(kw, StringComparison.OrdinalIgnoreCase)) / (double)denominator;

        double judgeScore = 0.2;
        Exception? judgeError = null;
        try
        {
            judgeScore = await JudgeSummaryAsync(
                judgeChatClient,
                evalCase.GoldReferenceSummary,
                summary,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            judgeError = ex;
        }

        // For Primary clusters the LLM sets the Type; for Utility the production summarizer
        // hardcodes ClusterType.Infrastructure, so we set PredictedType to null to keep
        // grading consistent with IsCorrectType (which skips type grading for Utility).
        ClusterType? predictedType = evalCase.Origin == ClusterMembershipKind.Utility
            ? null
            : cluster.Type;

        return new EvalResult(
            Case: evalCase,
            PredictedSummary: summary,
            PredictedType: predictedType,
            PredictedCoherence: cluster.CoherenceScore,
            KeywordRecall: keywordRecall,
            JudgeScore: judgeScore,
            Error: judgeError);
    }

    /// <summary>
    /// Asks the judge LLM to score the candidate summary against a gold reference on a 1-5
    /// scale, then maps the result to the [0, 1] range.  Throws on network or API errors;
    /// the caller in <see cref="RunOneAsync"/> catches those.
    /// </summary>
    /// <param name="judgeChatClient">Chat client acting as the judge.</param>
    /// <param name="goldReference">The expected gold-reference summary.</param>
    /// <param name="predictedSummary">The candidate summary produced by the summarizer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A score in [0, 1] (0.2 when parsing fails).</returns>
    private static async Task<double> JudgeSummaryAsync(
        IChatClient judgeChatClient,
        string goldReference,
        string predictedSummary,
        CancellationToken cancellationToken)
    {
        string prompt =
            $"""
            Grade the candidate cluster summary against the gold reference on a 1-5 scale.
            Gold: {goldReference}
            Candidate: {predictedSummary}
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
    /// Parses the judge response text to extract a numeric score in [0, 1].
    /// Returns <c>0.2</c> (= 1/5) when the expected <c>Score: N</c> line is absent or
    /// unparseable.
    /// </summary>
    /// <param name="text">The raw text returned by the judge LLM.</param>
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

    // ── Eval dataset ────────────────────────────────────────────────────────

    /// <summary>One labeled cluster: input fields plus expected output signals.</summary>
    internal sealed record EvalCase(
        string Label,
        ClusterMembershipKind Origin,
        IReadOnlyList<string> SymbolFqns,
        ClusterType? ExpectedType,
        IReadOnlyList<string> ExpectedKeywords,
        string GoldReferenceSummary,
        double ExpectedCoherence);

    /// <summary>Outcome of one <see cref="EvalCase"/> run through the summarizer.</summary>
    internal sealed record EvalResult(
        EvalCase Case,
        string PredictedSummary,
        ClusterType? PredictedType,
        double PredictedCoherence,
        double KeywordRecall,
        double JudgeScore,
        Exception? Error)
    {
        /// <summary>
        /// True when the predicted type matches the expected type, or when origin is Utility
        /// (the production summarizer hardcodes <see cref="ClusterType.Infrastructure"/> for
        /// utility clusters, so Type is not graded).
        /// </summary>
        public bool IsCorrectType =>
            this.Case.Origin == ClusterMembershipKind.Utility
                || (this.PredictedType is { } predicted && predicted == this.Case.ExpectedType);
    }

    /// <summary>
    /// Curated labeled dataset of cluster summarization cases drawn from actual symbols in
    /// this repository.
    /// </summary>
    internal static class EvalDataset
    {
        /// <summary>All nine eval cases covering Primary/Business, Primary/Infrastructure,
        /// Primary/Mixed, and Utility clusters.</summary>
        public static readonly IReadOnlyList<EvalCase> All =
        [
            // ── Primary / Business ──────────────────────────────────────────

            new EvalCase(
                Label: "Query.Retrieval",
                Origin: ClusterMembershipKind.Primary,
                SymbolFqns:
                [
                    "Agency.GraphRAG.Code.Query.QueryPipeline",
                    "Agency.GraphRAG.Code.Query.ContextAssembler",
                    "Agency.GraphRAG.Code.Query.HybridRetriever",
                    "Agency.GraphRAG.Code.Query.QueryPlanner",
                ],
                ExpectedType: ClusterType.Business,
                ExpectedKeywords: ["query", "retriev", "context", "rank"],
                GoldReferenceSummary: "Pipeline that takes a user question, plans and retrieves relevant code via hybrid search, assembles ranked context, and feeds it to the LLM.",
                ExpectedCoherence: 0.8),

            new EvalCase(
                Label: "Summarization",
                Origin: ClusterMembershipKind.Primary,
                SymbolFqns:
                [
                    "Agency.GraphRAG.Code.Cluster.ClusterSummarizer",
                    "Agency.GraphRAG.Code.Summarizer.SymbolSummarizer",
                    "Agency.GraphRAG.Code.Summarizer.SummarizationPromptBuilder",
                ],
                ExpectedType: ClusterType.Business,
                ExpectedKeywords: ["summar", "cluster", "symbol"],
                GoldReferenceSummary: "Drives LLM-based summarization of individual symbols and cluster groups, building prompts and aggregating results into structured summaries.",
                ExpectedCoherence: 0.8),

            // ── Primary / Infrastructure ────────────────────────────────────

            new EvalCase(
                Label: "Indexing",
                Origin: ClusterMembershipKind.Primary,
                SymbolFqns:
                [
                    "Agency.GraphRAG.Code.Walker.RepoWalker",
                    "Agency.GraphRAG.Code.ChangeDetector.ChangeDetector",
                    "Agency.GraphRAG.Code.Pipeline.IndexingPipeline",
                ],
                ExpectedType: ClusterType.Business,
                ExpectedKeywords: ["index", "repo", "walk", "change"],
                GoldReferenceSummary: "Orchestrates incremental repository indexing: walks the file tree, detects changed files, and drives the full pipeline that updates the code graph.",
                ExpectedCoherence: 0.8),

            new EvalCase(
                Label: "Graph stores",
                Origin: ClusterMembershipKind.Primary,
                SymbolFqns:
                [
                    "Agency.GraphRAG.Code.Sqlite.SqliteGraphStore",
                    "Agency.GraphRAG.Code.Postgres.PostgresGraphStore",
                    "Agency.GraphRAG.Code.Storage.IGraphStore",
                ],
                ExpectedType: ClusterType.Infrastructure,
                ExpectedKeywords: ["store", "graph", "persist"],
                GoldReferenceSummary: "Persistence layer for the code graph, offering SQLite and PostgreSQL implementations behind a common IGraphStore abstraction.",
                ExpectedCoherence: 0.8),

            new EvalCase(
                Label: "LLM clients",
                Origin: ClusterMembershipKind.Primary,
                SymbolFqns:
                [
                    "Agency.Llm.Claude.ClaudeClient",
                    "Agency.Llm.OpenAI.OpenAIClient",
                    "Agency.Llm.Common.IModelProvider",
                ],
                ExpectedType: ClusterType.Infrastructure,
                ExpectedKeywords: ["llm", "client", "chat"],
                GoldReferenceSummary: "Provider-agnostic LLM client layer with Claude and OpenAI implementations sharing the IModelProvider contract for chat and streaming calls.",
                ExpectedCoherence: 0.8),

            // ── Primary / Mixed ─────────────────────────────────────────────

            new EvalCase(
                Label: "Query + policies",
                Origin: ClusterMembershipKind.Primary,
                SymbolFqns:
                [
                    "Agency.GraphRAG.Code.Query.QueryClassifier",
                    "Agency.GraphRAG.Code.Query.QueryPlanner",
                    "Agency.GraphRAG.Code.Cluster.ClusterSummarizer",
                ],
                ExpectedType: ClusterType.Mixed,
                ExpectedKeywords: ["classif", "query", "policy"],
                GoldReferenceSummary: "Cross-layer coordination between query intent classification, query planning, and cluster summarization that spans both business and infrastructure concerns.",
                ExpectedCoherence: 0.6),

            // ── Utility ─────────────────────────────────────────────────────

            new EvalCase(
                Label: "Observability",
                Origin: ClusterMembershipKind.Utility,
                SymbolFqns:
                [
                    "Agency.GraphRAG.Code.Cluster.ClusterTuningInstrumentation",
                    "Agency.GraphRAG.Code.Cluster.ClusterOptions",
                ],
                ExpectedType: null,
                ExpectedKeywords: ["log", "telemetr", "observ"],
                GoldReferenceSummary: "Cross-cutting instrumentation and configuration for cluster tuning, exposing metrics and options consumed throughout the clustering pipeline.",
                ExpectedCoherence: 0.8),

            new EvalCase(
                Label: "Embedding contracts",
                Origin: ClusterMembershipKind.Utility,
                SymbolFqns:
                [
                    "Agency.Embeddings.Common.IEmbeddingGenerator",
                    "Agency.Embeddings.Common.BatchingEmbeddingGenerator",
                ],
                ExpectedType: null,
                ExpectedKeywords: ["embed", "vector", "batch"],
                GoldReferenceSummary: "Shared embedding generation contract and batching decorator used across multiple projects to generate vector embeddings.",
                ExpectedCoherence: 0.8),

            new EvalCase(
                Label: "LLM configuration",
                Origin: ClusterMembershipKind.Utility,
                SymbolFqns:
                [
                    "Agency.Llm.Common.LlmClientOptions",
                    "Agency.Llm.Common.Model",
                ],
                ExpectedType: null,
                ExpectedKeywords: ["option", "config", "model"],
                GoldReferenceSummary: "Shared LLM configuration primitives — connection options and model descriptor — used by all provider implementations.",
                ExpectedCoherence: 0.8),
        ];
    }

    // ── Eval report ─────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregated quality metrics over one run of the eval dataset.
    /// </summary>
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

        /// <summary>Gets the count of Primary cases where type prediction was null (parse failure).</summary>
        public int ParseFailureCount =>
            this._results.Count(static r => r.PredictedType is null && r.Case.Origin == ClusterMembershipKind.Primary);

        /// <summary>
        /// Gets type accuracy over Primary cases only: fraction of Primary cases where the predicted type
        /// matches the expected type. Returns <c>1.0</c> defensively when there are no Primary cases.
        /// </summary>
        public double TypeAccuracy
        {
            get
            {
                IReadOnlyList<EvalResult> primary = this._results.Where(static r => r.Case.Origin == ClusterMembershipKind.Primary).ToList();
                if (primary.Count == 0)
                {
                    return 1.0;
                }

                return primary.Count(static r => r.IsCorrectType) / (double)primary.Count;
            }
        }

        /// <summary>
        /// Gets mean summary quality: average of <c>0.5 * KeywordRecall + 0.5 * JudgeScore</c> across all results.
        /// Returns <c>0.0</c> on empty dataset.
        /// </summary>
        public double SummaryQuality =>
            this._results.Count == 0
                ? 0.0
                : this._results.Average(static r => (0.5 * r.KeywordRecall) + (0.5 * r.JudgeScore));

        /// <summary>Gets the composite score: <c>0.5 * TypeAccuracy + 0.5 * SummaryQuality</c>.</summary>
        public double CompositeScore => (0.5 * this.TypeAccuracy) + (0.5 * this.SummaryQuality);

        /// <summary>Gets mean absolute error between predicted and expected coherence across all results.</summary>
        public double MeanCoherenceError =>
            this._results.Count == 0
                ? 0.0
                : this._results.Average(static r => Math.Abs(r.PredictedCoherence - r.Case.ExpectedCoherence));

        /// <summary>Gets the individual case results that make up this report.</summary>
        public IReadOnlyList<EvalResult> Results => this._results;

        /// <summary>Creates a new <see cref="EvalReport"/> for the given model and results.</summary>
        /// <param name="modelId">The model identifier.</param>
        /// <param name="results">The eval results to aggregate.</param>
        public static EvalReport From(string modelId, IReadOnlyList<EvalResult> results) =>
            new(modelId, results);

        /// <summary>Renders a human-readable multi-line summary of the eval run.</summary>
        public string Render()
        {
            StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"ClusterSummarizer eval against model: {this._modelId}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Cases: {this._results.Count}  Composite: {this.CompositeScore:P1}  TypeAcc: {this.TypeAccuracy:P1}  SummaryQuality: {this.SummaryQuality:P1}  MeanCoherenceErr: {this.MeanCoherenceError:F2}  ParseFailures: {this.ParseFailureCount}");
            sb.AppendLine();
            sb.AppendLine("Per-case results:");

            const int labelWidth = 28;
            const int typeWidth = 18;
            foreach (EvalResult r in this._results)
            {
                string origin = r.Case.Origin == ClusterMembershipKind.Primary ? "[Primary]" : "[Utility]";
                string label = r.Case.Label.PadRight(labelWidth);
                string expectedType = (r.Case.ExpectedType?.ToString() ?? "N/A").PadRight(typeWidth);
                string predictedType = r.PredictedType?.ToString() ?? (r.Case.Origin == ClusterMembershipKind.Utility ? "N/A" : "<null>");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {origin} {label} {expectedType} -> {predictedType,-14}  KW: {r.KeywordRecall:P0}  Judge: {r.JudgeScore:P0}  Coherence: {r.PredictedCoherence:F2} (exp {r.Case.ExpectedCoherence:F2})");
            }

            IReadOnlyList<EvalResult> misses = this._results
                .Where(static r => !r.IsCorrectType || r.KeywordRecall < 0.5)
                .ToList();

            if (misses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Misses (Type mismatch or KeywordRecall < 0.5):");
                foreach (EvalResult miss in misses)
                {
                    string origin = miss.Case.Origin == ClusterMembershipKind.Primary ? "[Primary]" : "[Utility]";
                    string predictedType = miss.PredictedType?.ToString() ?? "<null>";
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"  {origin} {miss.Case.Label}  Expected: {miss.Case.ExpectedType}  Got: {predictedType}  KW: {miss.KeywordRecall:P0}");
                }
            }

            return sb.ToString();
        }
    }

    // ── Prompt variant types ─────────────────────────────────────────────────

    /// <summary>One named prompt design with builders for primary + utility cluster prompts.</summary>
    /// <param name="Name">Short name used as the row label in the comparison table.</param>
    /// <param name="Description">One-line description of what this variant is testing.</param>
    /// <param name="PrimaryBuilder">Prompt builder for primary (non-utility) clusters. Ignored when <paramref name="IsBaseline"/> is <see langword="true"/>.</param>
    /// <param name="UtilityBuilder">Prompt builder for utility clusters. Ignored when <paramref name="IsBaseline"/> is <see langword="true"/>.</param>
    /// <param name="IsBaseline">When <see langword="true"/>, the sweep constructs the summarizer with the default constructor so the production prompts are exercised verbatim.</param>
    internal sealed record PromptVariant(
        string Name,
        string Description,
        Func<ClusterSummaryRequest, string> PrimaryBuilder,
        Func<ClusterSummaryRequest, string> UtilityBuilder,
        bool IsBaseline);

    /// <summary>Aggregated outcome of running one <see cref="PromptVariant"/> across the dataset, possibly multiple times.</summary>
    /// <param name="Variant">The variant that was evaluated.</param>
    /// <param name="Reports">One eval report per run. Always contains at least one entry.</param>
    internal sealed record VariantOutcome(PromptVariant Variant, IReadOnlyList<EvalReport> Reports)
    {
        /// <summary>Gets the number of runs performed.</summary>
        public int RunCount => this.Reports.Count;

        /// <summary>Gets the mean composite score across all runs.</summary>
        public double MeanCompositeScore =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.CompositeScore);

        /// <summary>Gets the sample standard deviation of composite scores across runs.</summary>
        public double CompositeScoreStdDev => EvalTestHelpers.SampleStdDev(this.Reports.Select(static r => r.CompositeScore));

        /// <summary>Gets the mean type accuracy across all runs.</summary>
        public double MeanTypeAccuracy =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.TypeAccuracy);

        /// <summary>Gets the mean summary quality across all runs.</summary>
        public double MeanSummaryQuality =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.SummaryQuality);

        /// <summary>Gets the mean count of parse failures across all runs.</summary>
        public double MeanParseFailures =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => (double)r.ParseFailureCount);

        /// <summary>Gets the mean keyword recall across all runs and all cases.</summary>
        public double MeanKeywordRecall =>
            this.Reports.Count == 0 ? 0.0 :
            this.Reports.Average(report => report.Results.Average(r => r.KeywordRecall));

        /// <summary>Gets the mean judge score across all runs and all cases.</summary>
        public double MeanJudgeScore =>
            this.Reports.Count == 0 ? 0.0 :
            this.Reports.Average(report => report.Results.Average(r => r.JudgeScore));

    }

    // ── Prompt variants ──────────────────────────────────────────────────────

    /// <summary>
    /// Named prompt variants swept against the live model to compare summarization quality.
    /// Each entry probes a different hypothesis about prompting strategies.
    /// </summary>
    internal static class PromptVariants
    {
        private static string JoinSymbols(ClusterSummaryRequest request) =>
            string.Join(", ", request.Symbols.Select(static s => s.FullyQualifiedName ?? s.Name).OrderBy(static v => v, StringComparer.Ordinal));

        /// <summary>All 3 prompt variants to sweep.</summary>
        public static readonly IReadOnlyList<PromptVariant> All =
        [
            // 1. Baseline — production prompts (procedure + stronger anti-mixed tiebreaker + plain coherence, promoted 2026-05-14 after the 2x2 head-to-head sweep).
            new PromptVariant(
                Name: "Baseline",
                Description: "Production prompts (default constructor — procedure + stronger anti-mixed tiebreaker + plain `score 1-5 by how tightly symbols belong together`, promoted after the 2026-05-14 2x2 sweep).",
                PrimaryBuilder: static _ => string.Empty,
                UtilityBuilder: static _ => string.Empty,
                IsBaseline: true),

            // 2. RubricRollback — pre-promotion canary. Original anti-mixed tiebreaker, no stronger wording, plain coherence.
            //    Confirms the per-session anchor: if this scores ~71% (as in the 2x2 sweep), the new Baseline's +12pp lead replicates.
            new PromptVariant(
                Name: "RubricRollback",
                Description: "Per-session canary — original (weaker) anti-mixed tiebreaker, plain coherence wording, no rubric. Anchors absolute scoring: if Baseline − RubricRollback ≈ +12pp, the stronger tiebreaker's effect is replicating.",
                PrimaryBuilder: request =>
                    $"Decision procedure:\n" +
                    $"1. Read the symbol names and identify their unifying concept.\n" +
                    $"2. If the concept is a domain operation, choose business. If it's plumbing (logging, retries, persistence), choose infrastructure. If it spans both, choose mixed.\n" +
                    $"3. Tiebreaker: only choose mixed if you genuinely cannot pick business or infrastructure. Multiple implementations of the same role (e.g. SqliteFoo + PostgresFoo, ClaudeClient + OpenAIClient) are infrastructure, not mixed.\n" +
                    $"4. Score coherence 1-5 by how tightly the symbols belong together.\n" +
                    $"Now produce:\n" +
                    $"Label: {request.Label}\nSymbols: {JoinSymbols(request)}\nReturn:\nSummary: ...\nType: business|infrastructure|mixed\nCoherence: 1-5",
                UtilityBuilder: request =>
                    $"This cluster contains cross-cutting code used across the codebase. Describe its role, not a unifying business topic.\nLabel: {request.Label}\nSymbols: {JoinSymbols(request)}\nReturn:\nSummary: ...\nCoherence: 1-5",
                IsBaseline: false),

            // 3. WithRubric — adds the coherence rubric on top of new Baseline. Redundancy check.
            //    Previous 2x2 showed adding rubric to the stronger tiebreaker REDUCES composite by ~5pp (interaction = −12pp).
            //    This variant exists to verify that destructive interaction still holds; if it disappears, reconsider the rubric.
            new PromptVariant(
                Name: "WithRubric",
                Description: "New Baseline (stronger tiebreaker) PLUS the 1-5 coherence rubric. Redundancy check — the 2x2 sweep showed these interventions antagonize; this variant catches whether that interaction reverses in future sessions.",
                PrimaryBuilder: request =>
                    $"Decision procedure:\n" +
                    $"1. Read the symbol names and identify their unifying concept.\n" +
                    $"2. If the concept is a domain operation, choose business. If it's plumbing (logging, retries, persistence), choose infrastructure. If it spans both, choose mixed.\n" +
                    $"3. Tiebreaker: only choose mixed if the symbols genuinely belong to two unrelated domains. Multiple implementations of the same role (e.g. SqliteFoo + PostgresFoo, ClaudeClient + OpenAIClient) are infrastructure, not mixed. Do NOT choose mixed merely because a cluster contains many classes or spans several namespaces.\n" +
                    $"4. Score coherence 1-5: 1=unrelated symbols, 2=loosely related, 3=share a theme, 4=tightly scoped to one responsibility, 5=symbols form a complete, focused unit.\n" +
                    $"Now produce:\n" +
                    $"Label: {request.Label}\nSymbols: {JoinSymbols(request)}\nReturn:\nSummary: ...\nType: business|infrastructure|mixed\nCoherence: 1-5",
                UtilityBuilder: request =>
                    $"This cluster contains cross-cutting code used across the codebase. Describe its role, not a unifying business topic.\nLabel: {request.Label}\nSymbols: {JoinSymbols(request)}\nReturn:\nSummary: ...\nCoherence: 1-5",
                IsBaseline: false),
        ];
    }

    // ── Variant comparison rendering ─────────────────────────────────────────

    /// <summary>
    /// Renders a side-by-side comparison of multiple <see cref="VariantOutcome"/>s as a
    /// markdown table including composite scores, component metrics, and the winner delta vs baseline.
    /// </summary>
    internal static class VariantComparison
    {
        /// <summary>
        /// Renders the comparison report as a markdown string.
        /// </summary>
        /// <param name="modelId">The model identifier under test.</param>
        /// <param name="outcomes">The variant outcomes to compare.</param>
        /// <returns>A markdown-formatted comparison report.</returns>
        public static string Render(string modelId, IReadOnlyList<VariantOutcome> outcomes)
        {
            int runCount = outcomes.Count == 0 ? 0 : outcomes[0].RunCount;
            int caseCount = outcomes.Count == 0 ? 0 : outcomes[0].Reports[0].CaseCount;
            bool multiRun = runCount > 1;

            StringBuilder sb = new();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# ClusterSummarizer prompt-variant sweep against model: {modelId}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Variants: {outcomes.Count}  Cases per variant: {caseCount}  Runs per variant: {runCount}");
            sb.AppendLine();

            // Markdown table header.
            string compositeHeader = multiRun ? "Composite (±σ pp)" : "Composite";
            sb.AppendLine($"| Variant | {compositeHeader} | TypeAcc | SummaryQuality | KW recall mean | Judge mean | ParseFail (mean) |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

            // Markdown table rows.
            foreach (VariantOutcome outcome in outcomes)
            {
                string compositeCell = multiRun
                    ? $"{outcome.MeanCompositeScore:P1} ±{(outcome.CompositeScoreStdDev * 100.0).ToString("F1", CultureInfo.InvariantCulture)}"
                    : outcome.MeanCompositeScore.ToString("P1", CultureInfo.InvariantCulture);

                double meanParseFailures = outcome.MeanParseFailures;
                string parseCell = meanParseFailures == Math.Floor(meanParseFailures)
                    ? ((int)meanParseFailures).ToString(CultureInfo.InvariantCulture)
                    : meanParseFailures.ToString("F1", CultureInfo.InvariantCulture);

                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {outcome.Variant.Name} | {compositeCell} | {outcome.MeanTypeAccuracy:P1} | {outcome.MeanSummaryQuality:P1} | {outcome.MeanKeywordRecall:F2} | {outcome.MeanJudgeScore:F2} | {parseCell} |");
            }

            sb.AppendLine();
            sb.AppendLine("## Variant descriptions");
            sb.AppendLine();
            foreach (VariantOutcome outcome in outcomes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{outcome.Variant.Name}** — {outcome.Variant.Description}");
            }

            // Winner + delta vs baseline.
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
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Winner: **{winner.Variant.Name}** at {winnerScore}.");
            }

            return sb.ToString();
        }
    }

    // ── StubEmbeddingGenerator: returns a single-float vector ───────────────

    /// <summary>
    /// Minimal embedding generator for use in eval tests where embedding quality is irrelevant.
    /// Returns a one-element vector whose sole value is the input length.
    /// </summary>
    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator
    {
        /// <inheritdoc />
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { input.Length });

        /// <inheritdoc />
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Wraps an inner <see cref="IChatClient"/> and injects a default <see cref="ChatOptions.ModelId"/>
    /// when the caller does not supply one. Required because <c>OpenAIClient.CreateChatClient()</c>
    /// returns a model-agnostic client (the production codepath selects the model per-request via
    /// <see cref="ChatOptions.ModelId"/>), and <see cref="ClusterSummarizer"/> calls the chat client
    /// without specifying options — so without this wrapper LM Studio receives the literal model id
    /// <c>"default"</c> and returns HTTP 400 model_not_found.
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
    /// <see cref="ClusterSummarizer"/> under test. If the endpoint is unreachable a
    /// human-readable <see cref="SkipReason"/> is set and individual tests call
    /// <c>Assert.Skip</c>.
    /// </summary>
    public sealed class LiveSummarizerFixture : IAsyncLifetime
    {
        private IChatClient? _chatClient;
        private ClusterSummarizer? _summarizer;
        private IEmbeddingGenerator? _embeddingGenerator;

        /// <summary>Gets the resolved model id (from <c>Summarizer:StandardModel</c>) used for summarization.</summary>
        public string ModelId { get; private set; } = "unknown";

        /// <summary>Gets the live summarizer under test.</summary>
        /// <exception cref="InvalidOperationException">Thrown when setup failed; check <see cref="SkipReason"/>.</exception>
        public ClusterSummarizer Summarizer =>
            this._summarizer ?? throw new InvalidOperationException("Summarizer was not initialised; check SkipReason.");

        /// <summary>Gets the underlying chat client so prompt-variant tests can construct fresh summarizers.</summary>
        /// <exception cref="InvalidOperationException">Thrown when setup failed; check <see cref="SkipReason"/>.</exception>
        public IChatClient ChatClient =>
            this._chatClient ?? throw new InvalidOperationException("ChatClient was not initialised; check SkipReason.");

        /// <summary>Gets the stub embedding generator shared across tests.</summary>
        /// <exception cref="InvalidOperationException">Thrown when setup failed; check <see cref="SkipReason"/>.</exception>
        public IEmbeddingGenerator EmbeddingGenerator =>
            this._embeddingGenerator ?? throw new InvalidOperationException("EmbeddingGenerator was not initialised; check SkipReason.");

        /// <summary>Gets a non-empty reason when the endpoint is unreachable, otherwise <see langword="null"/>.</summary>
        public string? SkipReason { get; private set; }

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            try
            {
                IConfigurationRoot config = EvalTestHelpers.LoadCliConfiguration();
                string baseUrl = EvalTestHelpers.RequireConfig(config, $"{EvalTestHelpers.LlmClientSection}:BaseUrl");
                string apiKey = EvalTestHelpers.RequireConfig(config, $"{EvalTestHelpers.LlmClientSection}:ApiKey");
                this.ModelId = EvalTestHelpers.RequireConfig(config, $"{EvalTestHelpers.SummarizerSection}:StandardModel");

                LlmClientOptions options = new() { BaseUrl = baseUrl, ApiKey = apiKey };
                IChatClient rawChatClient = new OpenAIClient(Options.Create(options)).CreateChatClient();
                this._chatClient = new ModelIdDefaultingChatClient(rawChatClient, this.ModelId);
                this._embeddingGenerator = new StubEmbeddingGenerator();
                this._summarizer = new ClusterSummarizer(this._chatClient, this._embeddingGenerator);

                // Smoke-check with one trivial cluster so eval skips (not fails) when LM Studio is offline.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                Symbol probe = new()
                {
                    Id = Guid.NewGuid(),
                    FileId = Guid.NewGuid(),
                    Name = "Probe",
                    FullyQualifiedName = "Agency.Probe",
                    Kind = SymbolKind.Class,
                    IsUtility = false,
                    SourceRangeStart = 1,
                    SourceRangeEnd = 2,
                };
                ClusterSummaryRequest probeRequest = new(Guid.NewGuid(), "Probe", ClusterMembershipKind.Primary, [probe]);
                _ = await this._summarizer.SummarizeAsync([probeRequest], cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or ClientResultException)
            {
                this.SkipReason = $"Live LLM endpoint not reachable for ClusterSummarizer eval ({ex.GetType().Name}: {ex.Message}). Start LM Studio with the configured model loaded, then re-run.";
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            this._chatClient?.Dispose();
            return ValueTask.CompletedTask;
        }

    }
}
