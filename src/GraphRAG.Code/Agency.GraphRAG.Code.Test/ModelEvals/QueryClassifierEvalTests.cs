using System.Globalization;
using System.Text;
using Agency.GraphRAG.Code.Query;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agency.GraphRAG.Code.Test.ModelEvals;

/// <summary>
/// Evaluation suite for <see cref="QueryClassifier"/> against the live LLM endpoint
/// declared in <c>src\GraphRAG.Code\Agency.GraphRAG.Code.Cli\appsettings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the unit tests in <see cref="QueryClassifierTests"/>, this suite measures the
/// classification quality of the configured model against a labeled dataset of queries
/// drawn from <c>QueryExamples.md</c> plus paraphrased variants. It reports a confusion
/// matrix and asserts a minimum overall accuracy threshold so a regression in the deployed
/// model surfaces as a test failure rather than a silent quality drop.
/// </para>
/// <para>
/// Run with: <c>dotnet test --filter "Category=Eval"</c>. Requires LM Studio
/// (or another OpenAI-compatible endpoint) reachable at the URL configured in the CLI's
/// <c>appsettings.json</c>. Set the env var <c>AGENCY_CLASSIFIER_EVAL_THRESHOLD</c>
/// (e.g. <c>0.65</c>) to override the default pass threshold. The <c>Eval</c> trait
/// (distinct from <c>Functional</c>) keeps this slow, model-dependent suite out of both
/// the default <c>Category!=Functional</c> run and the broader <c>Category=Functional</c>
/// sweep so it only executes when explicitly requested.
/// </para>
/// </remarks>
[Trait("Category", "Eval")]
public sealed class QueryClassifierEvalTests(QueryClassifierEvalTests.LiveClassifierFixture fixture, ITestOutputHelper output)
    : IClassFixture<QueryClassifierEvalTests.LiveClassifierFixture>
{
    private const double DefaultOverallAccuracyThreshold = 0.70;
    private const string ThresholdEnvironmentVariable = "AGENCY_CLASSIFIER_EVAL_THRESHOLD";
    private const int DefaultRunCount = 3;
    private const int MaxRunCount = 20;
    private const string RunCountEnvironmentVariable = "AGENCY_CLASSIFIER_EVAL_RUNS";

    private readonly LiveClassifierFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Runs the full labeled dataset through the live classifier, prints a confusion matrix
    /// and per-category accuracy, and asserts the overall accuracy meets the threshold.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_AgainstConfiguredModel_MeetsAccuracyThreshold()
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
            EvalResult result = await this.RunOneAsync(evalCase, cancellationToken);
            results.Add(result);
        }

        EvalReport report = EvalReport.From(this._fixture.ModelId, results);
        this._output.WriteLine(report.Render());

        double threshold = ReadThreshold();
        Assert.True(
            report.OverallAccuracy >= threshold,
            $"Overall accuracy {report.OverallAccuracy:P1} fell below threshold {threshold:P1} on {this._fixture.ModelId}. See output for per-category breakdown.");
    }

    /// <summary>
    /// Per-case visibility: each labeled query becomes an individual test case so the
    /// test explorer surfaces which exact queries the model misclassifies.
    /// </summary>
    [Theory]
    [MemberData(nameof(EvalCaseData))]
    public async Task ClassifyAsync_PerCase_PredictsExpectedCategory(string query, QueryCategory expected)
    {
        if (!string.IsNullOrEmpty(this._fixture.SkipReason))
        {
            Assert.Skip(this._fixture.SkipReason);
        }

        EvalResult result = await this.RunOneAsync(new EvalCase(query, expected), TestContext.Current.CancellationToken);

        Assert.True(
            result.IsCorrect,
            $"Expected {expected} but got {(result.Predicted?.ToString() ?? "<unparseable>")} for query: \"{query}\". Raw response: \"{result.RawResponse}\".");
    }

    /// <summary>
    /// Sweeps a curated set of prompt variants against the same labeled dataset and the
    /// same live model, then prints a side-by-side comparison (overall accuracy, per-category
    /// recall, parse-failure count) and the winner's delta vs the baseline default prompts.
    /// Asserts only that the BEST variant clears the threshold so a losing experiment is
    /// reported, not failed.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_PromptVariantSweep_BestVariantBeatsThreshold()
    {
        if (!string.IsNullOrEmpty(this._fixture.SkipReason))
        {
            Assert.Skip(this._fixture.SkipReason);
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IReadOnlyList<EvalCase> dataset = EvalDataset.All;
        IReadOnlyList<PromptVariant> variants = PromptVariants.All;
        QueryOptions options = new() { CheapestModel = this._fixture.ModelId };
        int runCount = ReadRunCount();

        List<VariantOutcome> outcomes = new(variants.Count);
        foreach (PromptVariant variant in variants)
        {
            QueryClassifier classifier = variant.IsBaseline
                ? new QueryClassifier(this._fixture.ChatClient, options)
                : new QueryClassifier(this._fixture.ChatClient, options, variant.Instructions, variant.QueryPrompt);

            List<EvalReport> reports = new(runCount);
            for (int run = 0; run < runCount; run++)
            {
                List<EvalResult> results = new(dataset.Count);
                foreach (EvalCase evalCase in dataset)
                {
                    EvalResult result = await RunOneAsync(classifier, evalCase, cancellationToken);
                    results.Add(result);
                }

                reports.Add(EvalReport.From(this._fixture.ModelId, results));
            }

            outcomes.Add(new VariantOutcome(variant, reports));
        }

        string report = VariantComparison.Render(this._fixture.ModelId, outcomes);
        this._output.WriteLine(report);

        // Persist the report so the comparison table survives `dotnet test` without --logger.
        // Timestamped filename keeps a permanent history that supports diffing across runs.
        string reportsDir = Path.Combine(LiveClassifierFixture.FindRepoRoot(), "TestResults");
        Directory.CreateDirectory(reportsDir);
        string reportPath = Path.Combine(reportsDir, $"classifier-sweep-{DateTime.UtcNow:yyyy-MM-dd_HHmmss}Z.md");
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);
        this._output.WriteLine($"Report written to: {reportPath}");

        VariantOutcome winner = outcomes.OrderByDescending(static o => o.MeanAccuracy).First();
        double threshold = ReadThreshold();

        Assert.True(
            winner.MeanAccuracy >= threshold,
            $"Best prompt variant '{winner.Variant.Name}' achieved mean {winner.MeanAccuracy:P1} across {winner.RunCount} run(s), below threshold {threshold:P1} on {this._fixture.ModelId}. See output for full comparison.");
    }

    /// <summary>
    /// Theory data for <see cref="ClassifyAsync_PerCase_PredictsExpectedCategory"/>.
    /// </summary>
    public static TheoryData<string, QueryCategory> EvalCaseData()
    {
        TheoryData<string, QueryCategory> data = new();
        foreach (EvalCase evalCase in EvalDataset.All)
        {
            data.Add(evalCase.Query, evalCase.Expected);
        }

        return data;
    }

    private Task<EvalResult> RunOneAsync(EvalCase evalCase, CancellationToken cancellationToken) =>
        RunOneAsync(this._fixture.Classifier, evalCase, cancellationToken);

    private static async Task<EvalResult> RunOneAsync(QueryClassifier classifier, EvalCase evalCase, CancellationToken cancellationToken)
    {
        try
        {
            QueryCategory predicted = await classifier
                .ClassifyAsync(evalCase.Query, cancellationToken)
                .ConfigureAwait(false);
            return new EvalResult(evalCase, predicted, RawResponse: predicted.ToString(), Error: null);
        }
        catch (InvalidOperationException ex)
        {
            // QueryClassifier throws when the model returns a non-enum string. Treat as a miss.
            return new EvalResult(evalCase, Predicted: null, RawResponse: ExtractRawFromException(ex), Error: ex);
        }
    }

    private static string ExtractRawFromException(InvalidOperationException ex)
    {
        const string prefix = "Unsupported query category '";
        string message = ex.Message;
        int start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return message;
        }

        int valueStart = start + prefix.Length;
        int valueEnd = message.IndexOf('\'', valueStart);
        return valueEnd < 0 ? message[valueStart..] : message[valueStart..valueEnd];
    }

    private static double ReadThreshold()
    {
        string? raw = Environment.GetEnvironmentVariable(ThresholdEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed is >= 0.0 and <= 1.0)
        {
            return parsed;
        }

        return DefaultOverallAccuracyThreshold;
    }

    private static int ReadRunCount()
    {
        string? raw = Environment.GetEnvironmentVariable(RunCountEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 1 and <= MaxRunCount)
        {
            return parsed;
        }

        return DefaultRunCount;
    }

    // ── Eval dataset ─────────────────────────────────────────────────────────

    /// <summary>
    /// One labeled query: a question and the category it is intended to land in.
    /// </summary>
    /// <param name="Query">The user question to classify.</param>
    /// <param name="Expected">The category the classifier should produce.</param>
    internal sealed record EvalCase(string Query, QueryCategory Expected);

    /// <summary>
    /// Outcome of running one <see cref="EvalCase"/> through the classifier.
    /// </summary>
    /// <param name="Case">The case that was evaluated.</param>
    /// <param name="Predicted">The category the classifier produced, or <see langword="null"/> when the response was unparseable.</param>
    /// <param name="RawResponse">The raw text returned by the model, for diagnostics.</param>
    /// <param name="Error">The exception thrown when classification failed, if any.</param>
    internal sealed record EvalResult(EvalCase Case, QueryCategory? Predicted, string RawResponse, Exception? Error)
    {
        public bool IsCorrect => this.Predicted is { } predicted && predicted == this.Case.Expected;
    }

    /// <summary>
    /// Curated labeled dataset. Cases are drawn from <c>QueryExamples.md</c> plus paraphrased
    /// variants designed to stress-test the classifier on phrasings that are close to a
    /// neighboring category.
    /// </summary>
    internal static class EvalDataset
    {
        public static readonly IReadOnlyList<EvalCase> All =
        [
            // Local — concrete behavior of a specific symbol.
            new("How does QueryPipeline assemble context?", QueryCategory.Local),
            new("What does HybridRetriever do when SymbolVectorSearch returns no results?", QueryCategory.Local),
            new("How does ContextAssembler enforce the token budget?", QueryCategory.Local),
            new("What does SuppressThinkingPipelinePolicy do?", QueryCategory.Local),
            new("How is the FocusTerm computed for Impact and Dependency queries?", QueryCategory.Local),

            // Subsystem — a slice of the codebase spanning several types.
            new("Walk me through the query pipeline from CLI input to LLM response", QueryCategory.Subsystem),
            new("Explain how the indexing pipeline turns source files into graph nodes", QueryCategory.Subsystem),
            new("How does the summarizer subsystem produce one-line and full summaries?", QueryCategory.Subsystem),
            new("Describe the scope resolver and how it disambiguates references", QueryCategory.Subsystem),

            // Global — repo-wide architectural map.
            new("What are the major subsystems in this repository?", QueryCategory.Global),
            new("Give me an architectural overview of the codebase", QueryCategory.Global),
            new("What are the main business domains in this solution?", QueryCategory.Global),
            new("Summarize the responsibilities of each top-level project", QueryCategory.Global),

            // Impact — callers/dependents of a symbol.
            new("What breaks if I change RepoWalker?", QueryCategory.Impact),
            new("Who depends on QueryPipeline?", QueryCategory.Impact),
            new("What are the callers of IncrementalHydrator?", QueryCategory.Impact),
            new("What would be affected by renaming SymbolSummarizer?", QueryCategory.Impact),

            // Dependency — what a symbol/module relies on.
            new("What does QueryPipeline depend on?", QueryCategory.Dependency),
            new("Which packages does Agency.GraphRAG.Code.Postgres import?", QueryCategory.Dependency),
            new("List the modules referenced by IndexingPipeline", QueryCategory.Dependency),
            new("What does SqliteGraphStore depend on?", QueryCategory.Dependency),
        ];
    }

    // ── Prompt variants ──────────────────────────────────────────────────────

    /// <summary>
    /// One labeled prompt design: a human-readable name plus the <c>instructions</c> and
    /// <c>queryPrompt</c> strings that <see cref="QueryClassifier"/> will use. The
    /// <see cref="IsBaseline"/> flag tells the sweep to call the no-arg constructor so the
    /// production defaults are exercised verbatim instead of being duplicated here.
    /// </summary>
    /// <param name="Name">Short name used as the row label in the comparison table.</param>
    /// <param name="Description">One-line description of what this variant is testing.</param>
    /// <param name="Instructions">System-role instructions for the classifier. Ignored when <paramref name="IsBaseline"/> is <see langword="true"/>.</param>
    /// <param name="QueryPrompt">User-role prompt prefix prepended to the query. Ignored when <paramref name="IsBaseline"/> is <see langword="true"/>.</param>
    /// <param name="IsBaseline">When <see langword="true"/>, the sweep constructs the classifier without prompts so the production defaults are used.</param>
    internal sealed record PromptVariant(
        string Name,
        string Description,
        string Instructions,
        string QueryPrompt,
        bool IsBaseline);

    /// <summary>
    /// Aggregated outcome of running one <see cref="PromptVariant"/> across the dataset, possibly
    /// multiple times. Holds one <see cref="EvalReport"/> per run and exposes mean / sample
    /// standard deviation accessors so the comparison rendering can show a stable signal rather
    /// than a noisy single sample.
    /// </summary>
    /// <param name="Variant">The variant that was evaluated.</param>
    /// <param name="Reports">One eval report per run. Always contains at least one entry.</param>
    internal sealed record VariantOutcome(PromptVariant Variant, IReadOnlyList<EvalReport> Reports)
    {
        public int RunCount => this.Reports.Count;

        public double MeanAccuracy =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => r.OverallAccuracy);

        public double AccuracyStdDev => SampleStdDev(this.Reports.Select(static r => r.OverallAccuracy));

        public double MeanRecallFor(QueryCategory category)
        {
            double sum = 0.0;
            int count = 0;
            foreach (EvalReport report in this.Reports)
            {
                double? recall = report.RecallFor(category);
                if (recall is not null)
                {
                    sum += recall.Value;
                    count++;
                }
            }

            return count == 0 ? 0.0 : sum / count;
        }

        public double MeanParseFailures =>
            this.Reports.Count == 0 ? 0.0 : this.Reports.Average(static r => (double)r.ParseFailureCount);

        private static double SampleStdDev(IEnumerable<double> values)
        {
            double[] sample = values.ToArray();
            if (sample.Length <= 1)
            {
                return 0.0;
            }

            double mean = sample.Average();
            double sumSquares = sample.Sum(value => (value - mean) * (value - mean));
            return Math.Sqrt(sumSquares / (sample.Length - 1));
        }
    }

    /// <summary>
    /// The set of prompt designs swept against the live model. Each entry probes a different
    /// hypothesis about why the cheap classifier model might be making mistakes (schema
    /// placement, verbosity, definitions, in-context examples, output-format discipline).
    /// </summary>
    internal static class PromptVariants
    {
        private const string DefinedCategoriesInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Categories:
            - Local: a question about the concrete behavior of a specific named symbol, method, or class.
            - Subsystem: a question about a slice of the codebase that spans several types (e.g. "the X pipeline", "the X subsystem").
            - Global: a repo-wide architectural question (overview, major subsystems, top-level projects).
            - Impact: who calls or depends on a named symbol; what would break if it changed.
            - Dependency: what a named symbol depends on, imports, or references.
            """;

        // Few-shot examples are deliberately paraphrased so they do NOT appear verbatim in
        // EvalDataset.All. Each example varies both the named symbol AND the verb framing
        // relative to the dataset cases, so a win for this variant reflects pattern
        // generalization rather than literal-string recognition of contaminated examples.
        private const string FewShotInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name. Categories: Local, Subsystem, Global, Impact, Dependency.

            Examples:
            Q: What does Phase1Writer.WriteAsync do?
            A: Local

            Q: How does the chunker subsystem split source files into symbol-level chunks?
            A: Subsystem

            Q: List the major layers of this codebase
            A: Global

            Q: Where is ChangeDetector used in the codebase?
            A: Impact

            Q: What does ManifestParserOrchestrator rely on?
            A: Dependency
            """;

        private const string MinimalQueryPrompt = "Query:";

        // Hypothesis: list order anchors the model toward earlier categories on borderline cases;
        // moving Local to the top should improve Local recall without hurting the other buckets
        // because their definitions remain unchanged.
        private const string DefinedCategoriesLocalFirstInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Categories:
            - Local: a question about the concrete behavior of a specific named symbol, method, or class.
            - Impact: who calls or depends on a named symbol; what would break if it changed.
            - Dependency: what a named symbol depends on, imports, or references.
            - Subsystem: a question about a slice of the codebase that spans several types (e.g. "the X pipeline", "the X subsystem").
            - Global: a repo-wide architectural question (overview, major subsystems, top-level projects).
            """;

        // Hypothesis: the misclassifications are Local -> Subsystem leaks because Local lacks a
        // negative criterion. Explicitly stating that questions about one symbol's behavior are
        // Local even when collaborators are mentioned should pull borderline cases back to Local.
        private const string DefinedCategoriesWithNegativesInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Categories:
            - Local: a question about the concrete behavior of a specific named symbol, method, or class. A question is still Local when it names a single primary symbol whose behavior is being asked about, even if other collaborators are mentioned in passing.
            - Subsystem: a question about a slice of the codebase that spans several types working together as a pipeline or process (e.g. "the X pipeline", "the X subsystem"). Not Local: the focus is the interaction, not one symbol's behavior.
            - Global: a repo-wide architectural question (overview, major subsystems, top-level projects).
            - Impact: who calls or depends on a named symbol; what would break if it changed.
            - Dependency: what a named symbol depends on, imports, or references.
            """;

        // Hypothesis: queries that literally contain a category name (e.g. "Impact and Dependency
        // queries") anchor the model toward that bucket even when the actual question is Local.
        // An explicit tiebreaker telling the model to classify by intent rather than by surface
        // keyword should fix the FocusTerm-style case without affecting the others.
        private const string DefinedCategoriesWithIntentTiebreakerInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Categories:
            - Local: a question about the concrete behavior of a specific named symbol, method, or class.
            - Subsystem: a question about a slice of the codebase that spans several types (e.g. "the X pipeline", "the X subsystem").
            - Global: a repo-wide architectural question (overview, major subsystems, top-level projects).
            - Impact: who calls or depends on a named symbol; what would break if it changed.
            - Dependency: what a named symbol depends on, imports, or references.

            Tiebreaker: if the query mentions a category word (e.g. "impact", "dependency", "subsystem", "global"), classify by the intent of the question, not by the keyword that appears. A question about HOW a value is computed is Local even if Impact or Dependency is mentioned as context.
            """;

        // Hypothesis: a short explicit decision procedure forces the model to identify the
        // subject of the question first, which should robustly route questions about one named
        // symbol's behavior to Local regardless of other words in the sentence.
        private const string DefinedCategoriesWithDecisionProcedureInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Decision procedure:
            1. Identify the subject of the question (what is being asked ABOUT).
            2. If the subject is one named symbol and the question asks how it works or what it does: Local.
            3. If the subject is one named symbol and the question asks who uses it or what would break: Impact.
            4. If the subject is one named symbol and the question asks what it uses, imports, or relies on: Dependency.
            5. If the subject is a named pipeline, subsystem, or process spanning several types: Subsystem.
            6. If the subject is the repository as a whole (overview, major parts, top-level projects): Global.

            Categories: Local, Subsystem, Global, Impact, Dependency.
            """;

        // Hypothesis: replacing prose definitions with question-SHAPE templates gives the model
        // a syntactic pattern to match, which is easier to apply than semantic judgement. Avoids
        // contamination by using shape variables (X, Y) rather than concrete identifiers.
        private const string DefinedCategoriesWithShapesInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Categories (shapes shown with X as a single symbol, Y as a behavior):
            - Local: "how does X do Y", "what does X do", "what does X do when Z". X is one symbol whose behavior is being asked about.
            - Subsystem: "walk me through the X pipeline", "explain the X subsystem", "describe how the X process works". X is a named slice spanning multiple types.
            - Global: "what are the major subsystems", "give an architectural overview", "what are the top-level projects". No single symbol is named.
            - Impact: "who calls X", "what breaks if X changes", "what depends on X". X is one symbol; the question is about its callers.
            - Dependency: "what does X depend on", "which packages does X import", "what does X reference". X is one symbol; the question is about what it uses.
            """;

        // Hypothesis: an explicit Local-preferring tiebreaker for ambiguous Local-vs-Subsystem
        // cases should recover the 60% Local recall. Local is the most specific bucket, so
        // breaking ties toward Local should not steal cases from genuinely broader buckets.
        private const string DefinedCategoriesPreferLocalInstructions =
            """
            Classify the user's codebase question into exactly one category and return only the category name.

            Categories:
            - Local: a question about the concrete behavior of a specific named symbol, method, or class.
            - Subsystem: a question about a slice of the codebase that spans several types (e.g. "the X pipeline", "the X subsystem").
            - Global: a repo-wide architectural question (overview, major subsystems, top-level projects).
            - Impact: who calls or depends on a named symbol; what would break if it changed.
            - Dependency: what a named symbol depends on, imports, or references.

            Tiebreaker: when a question could plausibly be Local or Subsystem, prefer Local. Subsystem requires the question to be about two or more named components working together, not a single symbol whose implementation touches collaborators.
            """;

        public static readonly IReadOnlyList<PromptVariant> All =
        [
            new PromptVariant(
                Name: "DefinedCategories",
                Description: "One-line definition per category to disambiguate Local vs Subsystem and Impact vs Dependency.",
                Instructions: DefinedCategoriesInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "FewShot",
                Description: "In-context Q/A examples, one per category. Kept as the only meaningful alternative after FewShotExtended and StrictFormat were eliminated by the 3-run sweep.",
                Instructions: FewShotInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "DefinedCategoriesLocalFirst",
                Description: "Tests whether list order anchors the model: moving Local to the top of the category list should bias borderline cases toward Local.",
                Instructions: DefinedCategoriesLocalFirstInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "DefinedCategoriesWithNegatives",
                Description: "Tests whether adding a negative criterion to Local (single-symbol focus even when collaborators are mentioned) recovers Local recall.",
                Instructions: DefinedCategoriesWithNegativesInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "DefinedCategoriesWithIntentTiebreaker",
                Description: "Tests whether an explicit 'classify by intent, not by surface keyword' rule fixes queries that mention a category name in their text.",
                Instructions: DefinedCategoriesWithIntentTiebreakerInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "DefinedCategoriesWithDecisionProcedure",
                Description: "Tests whether a short subject-first decision procedure robustly routes single-symbol questions to Local regardless of surrounding context words.",
                Instructions: DefinedCategoriesWithDecisionProcedureInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "DefinedCategoriesWithShapes",
                Description: "Tests whether replacing prose definitions with abstract question-shape templates gives the model a more reliable syntactic pattern to match.",
                Instructions: DefinedCategoriesWithShapesInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),

            new PromptVariant(
                Name: "DefinedCategoriesPreferLocal",
                Description: "Tests whether an explicit Local-vs-Subsystem tiebreaker preferring Local recovers the missing 40% of Local recall without stealing from broader buckets.",
                Instructions: DefinedCategoriesPreferLocalInstructions,
                QueryPrompt: MinimalQueryPrompt,
                IsBaseline: false),
        ];
    }

    // ── Variant comparison rendering ─────────────────────────────────────────

    /// <summary>
    /// Renders a side-by-side comparison of multiple <see cref="VariantOutcome"/>s as a
    /// human-readable table for the test output, including the winner and its delta against
    /// the baseline.
    /// </summary>
    internal static class VariantComparison
    {
        public static string Render(string modelId, IReadOnlyList<VariantOutcome> outcomes)
        {
            IReadOnlyList<QueryCategory> categories = Enum.GetValues<QueryCategory>();
            int runCount = outcomes.Count == 0 ? 0 : outcomes[0].RunCount;
            int caseCount = outcomes.Count == 0 ? 0 : outcomes[0].Reports[0].CaseCount;
            bool multiRun = runCount > 1;

            StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"QueryClassifier prompt-variant sweep against model: {modelId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Variants: {outcomes.Count}  Cases per variant: {caseCount}  Runs per variant: {runCount}");
            sb.AppendLine();

            int nameWidth = Math.Max(8, outcomes.Max(static o => o.Variant.Name.Length) + 2);
            int categoryWidth = categories.Max(static c => c.ToString().Length) + 2;
            // "100.0 % ±12.3" needs ~16 chars; single-run "100.0 %" needs ~10.
            int accuracyWidth = multiRun ? 16 : 12;

            // Header.
            sb.Append("Variant".PadRight(nameWidth));
            sb.Append((multiRun ? "Accuracy (±σ)" : "Accuracy").PadRight(accuracyWidth));
            foreach (QueryCategory category in categories)
            {
                sb.Append(category.ToString().PadRight(categoryWidth));
            }

            sb.Append("ParseFail");
            sb.AppendLine();

            // Rows.
            foreach (VariantOutcome outcome in outcomes)
            {
                sb.Append(outcome.Variant.Name.PadRight(nameWidth));
                string accuracyCell = multiRun
                    ? $"{outcome.MeanAccuracy:P1} ±{(outcome.AccuracyStdDev * 100.0).ToString("F1", CultureInfo.InvariantCulture)}"
                    : outcome.MeanAccuracy.ToString("P1", CultureInfo.InvariantCulture);
                sb.Append(accuracyCell.PadRight(accuracyWidth));
                foreach (QueryCategory category in categories)
                {
                    double recall = outcome.MeanRecallFor(category);
                    sb.Append(recall.ToString("P0", CultureInfo.InvariantCulture).PadRight(categoryWidth));
                }

                // Mean parse failures rounded to 1 decimal (or integer when whole).
                double meanParseFailures = outcome.MeanParseFailures;
                string parseCell = meanParseFailures == Math.Floor(meanParseFailures)
                    ? ((int)meanParseFailures).ToString(CultureInfo.InvariantCulture)
                    : meanParseFailures.ToString("F1", CultureInfo.InvariantCulture);
                sb.Append(parseCell);
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("Variant descriptions:");
            foreach (VariantOutcome outcome in outcomes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {outcome.Variant.Name,-18} {outcome.Variant.Description}");
            }

            // Winner + delta vs baseline (using mean accuracy across runs).
            VariantOutcome winner = outcomes.OrderByDescending(static o => o.MeanAccuracy).First();
            VariantOutcome? baseline = outcomes.FirstOrDefault(static o => o.Variant.IsBaseline);

            sb.AppendLine();
            string winnerAccuracy = multiRun
                ? $"{winner.MeanAccuracy:P1} (±{(winner.AccuracyStdDev * 100.0).ToString("F1", CultureInfo.InvariantCulture)} pp σ over {winner.RunCount} runs)"
                : winner.MeanAccuracy.ToString("P1", CultureInfo.InvariantCulture);
            if (baseline is not null)
            {
                double deltaPoints = (winner.MeanAccuracy - baseline.MeanAccuracy) * 100.0;
                string sign = deltaPoints >= 0 ? "+" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Winner: {winner.Variant.Name} at {winnerAccuracy} ({sign}{deltaPoints.ToString("F1", CultureInfo.InvariantCulture)} pp vs Baseline {baseline.MeanAccuracy:P1}).");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Winner: {winner.Variant.Name} at {winnerAccuracy} (no baseline in sweep).");
            }

            return sb.ToString();
        }
    }

    // ── Report rendering ─────────────────────────────────────────────────────

    /// <summary>
    /// Aggregated metrics over a run of the eval dataset.
    /// </summary>
    internal sealed class EvalReport
    {
        private readonly string _modelId;
        private readonly IReadOnlyList<EvalResult> _results;
        private readonly IReadOnlyList<QueryCategory> _categories;

        private EvalReport(string modelId, IReadOnlyList<EvalResult> results)
        {
            this._modelId = modelId;
            this._results = results;
            this._categories = Enum.GetValues<QueryCategory>();
        }

        public double OverallAccuracy =>
            this._results.Count == 0
                ? 0.0
                : this._results.Count(static r => r.IsCorrect) / (double)this._results.Count;

        /// <summary>Gets the total number of evaluated cases.</summary>
        public int CaseCount => this._results.Count;

        /// <summary>Gets the count of cases where the classifier returned an unparseable response.</summary>
        public int ParseFailureCount => this._results.Count(static r => r.Predicted is null);

        /// <summary>
        /// Returns recall (correct / labeled) for the given category, or <see langword="null"/>
        /// when the dataset contained no cases for that category.
        /// </summary>
        /// <param name="category">The expected category to compute recall for.</param>
        /// <returns>Recall in [0, 1], or <see langword="null"/> when the category is absent from the dataset.</returns>
        public double? RecallFor(QueryCategory category)
        {
            int labeled = this._results.Count(r => r.Case.Expected == category);
            if (labeled == 0)
            {
                return null;
            }

            int correct = this._results.Count(r => r.Case.Expected == category && r.IsCorrect);
            return correct / (double)labeled;
        }

        public static EvalReport From(string modelId, IReadOnlyList<EvalResult> results) =>
            new(modelId, results);

        public string Render()
        {
            StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"QueryClassifier eval against model: {this._modelId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Cases: {this._results.Count}  Correct: {this._results.Count(static r => r.IsCorrect)}  Overall accuracy: {this.OverallAccuracy:P1}");
            sb.AppendLine();

            // Per-category accuracy (recall: of all cases labeled C, how many did we get right).
            sb.AppendLine("Per-category recall:");
            foreach (QueryCategory expected in this._categories)
            {
                IReadOnlyList<EvalResult> forCategory = this._results.Where(r => r.Case.Expected == expected).ToList();
                if (forCategory.Count == 0)
                {
                    continue;
                }

                int correct = forCategory.Count(static r => r.IsCorrect);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {expected,-11} {correct}/{forCategory.Count} = {correct / (double)forCategory.Count:P1}");
            }

            sb.AppendLine();
            sb.AppendLine("Confusion matrix (rows = expected, columns = predicted):");
            int columnWidth = this._categories.Max(static c => c.ToString().Length) + 2;
            sb.Append(new string(' ', columnWidth));
            foreach (QueryCategory predicted in this._categories)
            {
                sb.Append(predicted.ToString().PadRight(columnWidth));
            }

            sb.Append("(parse-fail)");
            sb.AppendLine();

            foreach (QueryCategory expected in this._categories)
            {
                sb.Append(expected.ToString().PadRight(columnWidth));
                foreach (QueryCategory predicted in this._categories)
                {
                    int count = this._results.Count(r => r.Case.Expected == expected && r.Predicted == predicted);
                    sb.Append(count.ToString(CultureInfo.InvariantCulture).PadRight(columnWidth));
                }

                int parseFailures = this._results.Count(r => r.Case.Expected == expected && r.Predicted is null);
                sb.Append(parseFailures.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            // Highlight individual misses for quick diagnosis.
            IReadOnlyList<EvalResult> misses = this._results.Where(static r => !r.IsCorrect).ToList();
            if (misses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Misclassified queries:");
                foreach (EvalResult miss in misses)
                {
                    string predicted = miss.Predicted?.ToString() ?? $"<unparseable: \"{miss.RawResponse}\">";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  [{miss.Case.Expected} -> {predicted}] {miss.Case.Query}");
                }
            }

            return sb.ToString();
        }
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared fixture that locates the CLI's <c>appsettings.json</c>, builds an
    /// <see cref="IChatClient"/> against the configured LM Studio endpoint, and wires up the
    /// <see cref="QueryClassifier"/> under test. If the endpoint is unreachable a
    /// human-readable <see cref="SkipReason"/> is set and individual tests call
    /// <c>Assert.Skip</c>.
    /// </summary>
    public sealed class LiveClassifierFixture : IAsyncLifetime
    {
        private const string CliAppSettingsRelativePath = @"src\GraphRAG.Code\Agency.GraphRAG.Code.Cli\appsettings.json";
        private const string LlmClientSection = "LlmClient";
        private const string SummarizerSection = "Summarizer";

        private IChatClient? _chatClient;
        private QueryClassifier? _classifier;

        /// <summary>Gets the resolved model id (from <c>Summarizer:CheapestModel</c>) used for classification.</summary>
        public string ModelId { get; private set; } = "unknown";

        /// <summary>Gets the live classifier under test, or <see langword="null"/> if setup was skipped.</summary>
        public QueryClassifier Classifier =>
            this._classifier ?? throw new InvalidOperationException("Classifier was not initialised; check SkipReason.");

        /// <summary>Gets the underlying chat client so prompt-variant tests can construct fresh classifiers.</summary>
        public IChatClient ChatClient =>
            this._chatClient ?? throw new InvalidOperationException("ChatClient was not initialised; check SkipReason.");

        /// <summary>Gets a non-empty reason when the endpoint is unreachable, otherwise <see langword="null"/>.</summary>
        public string? SkipReason { get; private set; }

        public async ValueTask InitializeAsync()
        {
            try
            {
                IConfigurationRoot config = LoadCliConfiguration();

                string baseUrl = RequireConfig(config, $"{LlmClientSection}:BaseUrl");
                string apiKey = RequireConfig(config, $"{LlmClientSection}:ApiKey");
                this.ModelId = RequireConfig(config, $"{SummarizerSection}:CheapestModel");

                LlmClientOptions options = new() { BaseUrl = baseUrl, ApiKey = apiKey };
                this._chatClient = new OpenAIClient(Options.Create(options)).CreateChatClient();
                this._classifier = new QueryClassifier(this._chatClient, new QueryOptions { CheapestModel = this.ModelId });

                // Smoke-check the endpoint with a trivial classification so the eval is skipped
                // (not failed) when LM Studio is offline or the configured model is not loaded.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                _ = await this._classifier.ClassifyAsync("What does this method do?", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
            {
                this.SkipReason =
                    $"Live LLM endpoint not reachable for QueryClassifier eval ({ex.GetType().Name}: {ex.Message}). " +
                    "Start LM Studio with the configured model loaded, then re-run.";
            }
        }

        public ValueTask DisposeAsync()
        {
            this._chatClient?.Dispose();
            return ValueTask.CompletedTask;
        }

        private static IConfigurationRoot LoadCliConfiguration()
        {
            string repoRoot = FindRepoRoot();
            string appSettingsPath = Path.Combine(repoRoot, CliAppSettingsRelativePath);
            if (!File.Exists(appSettingsPath))
            {
                throw new FileNotFoundException(
                    $"CLI appsettings.json not found at expected location.",
                    appSettingsPath);
            }

            return new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(appSettingsPath)!)
                .AddJsonFile(Path.GetFileName(appSettingsPath), optional: false)
                .AddEnvironmentVariables()
                .Build();
        }

        private static string RequireConfig(IConfiguration configuration, string key) =>
            configuration[key]
                ?? throw new InvalidOperationException($"Missing required configuration value '{key}' in CLI appsettings.json.");

        internal static string FindRepoRoot()
        {
            string current = AppContext.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, "src", "Agency.slnx")))
                {
                    return current;
                }

                DirectoryInfo? parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }

            throw new InvalidOperationException("Could not locate the repository root containing src\\Agency.slnx.");
        }
    }
}
