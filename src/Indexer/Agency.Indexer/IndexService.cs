using System.Diagnostics;
using Agency.Embeddings.Common;
using Agency.Ingestion;
using Agency.VectorStore.Common;

namespace Agency.Indexer;

/// <summary>Arguments of an <c>index</c> run.</summary>
/// <param name="Index">The canonical index name.</param>
/// <param name="Root">The directory to index, or <see langword="null"/> to reuse the index's stored root.</param>
/// <param name="Extensions">The extensions to select, or <see langword="null"/> to reuse the stored (or default) selection.</param>
/// <param name="Names">The extensionless file names to select, or <see langword="null"/> to reuse the stored (or default) selection.</param>
/// <param name="MaxFileBytes">The per-file size cap.</param>
/// <param name="Wait">Whether to wait for another writer to finish instead of returning <see cref="IndexStatus.Locked"/>.</param>
/// <param name="Exclude">Globs (relative to the root) of files and folders to leave out, or <see langword="null"/> to reuse the stored selection.</param>
/// <param name="Rebuild">Whether to re-embed every file, even unchanged ones, and so allow the embedding model to change.</param>
internal sealed record IndexRequest(
    string Index,
    string? Root,
    IReadOnlyList<string>? Extensions,
    IReadOnlyList<string>? Names,
    long MaxFileBytes,
    bool Wait,
    IReadOnlyList<string>? Exclude = null,
    bool Rebuild = false);

/// <summary>Outcome of a write command.</summary>
internal enum IndexStatus
{
    /// <summary>The command completed and every file was processed.</summary>
    Ok,

    /// <summary>The command completed but at least one file failed.</summary>
    PartialFailure,

    /// <summary>Another process holds the writer lock for the index.</summary>
    Locked,
}

/// <summary>A file that could not be indexed.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Reason">Why it failed: the exception chain, and for an HTTP failure the status and the start of the response body.</param>
internal sealed record FailedFile(string Path, string Reason);

/// <summary>Result of an <c>index</c> run.</summary>
internal sealed record IndexResult(
    IndexStatus Status,
    string Index,
    string? Root,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Removed,
    int Unchanged,
    IReadOnlyList<string> SkippedTooLarge,
    IReadOnlyList<FailedFile> Failed,
    int ChunksWritten,
    long DurationMs);

/// <summary>What an <c>index --dry-run</c> would do. Nothing is written.</summary>
/// <param name="Index">The canonical index name.</param>
/// <param name="Root">The directory that would be indexed.</param>
/// <param name="Added">Files that would be indexed for the first time.</param>
/// <param name="Changed">Files that would be re-indexed.</param>
/// <param name="Removed">Files that would be removed from the index.</param>
/// <param name="Unchanged">The number of files that would be left alone.</param>
/// <param name="SkippedTooLarge">Files over the size cap.</param>
/// <param name="EstimatedChunks">The chunks the added and changed files split into.</param>
/// <param name="EstimatedSeconds">A rough embedding time from a small timed sample, or <see langword="null"/> when there was nothing to embed.</param>
internal sealed record DryRunResult(
    string Index,
    string Root,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Removed,
    int Unchanged,
    IReadOnlyList<string> SkippedTooLarge,
    int EstimatedChunks,
    double? EstimatedSeconds);

/// <summary>One semantic search hit.</summary>
/// <param name="Path">The full path of the source file.</param>
/// <param name="Chunk">The chunk index within the file.</param>
/// <param name="Score">Cosine similarity in [0, 1]; higher is closer.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Heading">The Markdown heading path above the chunk, when the index recorded one.</param>
/// <param name="StartLine">The 1-based first line of the chunk in the file, when the index recorded one.</param>
/// <param name="EndLine">The 1-based last line of the chunk in the file, when the index recorded one.</param>
/// <param name="Index">The index the hit came from.</param>
/// <param name="ExactMatch">Whether the chunk contains an identifier-like token of the query (set by hybrid ranking only).</param>
internal sealed record SearchResultHit(
    string Path,
    long? Chunk,
    double Score,
    string Text,
    string? Heading = null,
    long? StartLine = null,
    long? EndLine = null,
    string? Index = null,
    bool ExactMatch = false);

/// <summary>The score distribution of unrelated queries against an index.</summary>
/// <param name="Index">The calibrated index.</param>
/// <param name="Probes">How many unrelated queries were run.</param>
/// <param name="NoiseCeiling">The best score any unrelated query reached: anything at or below it is noise.</param>
/// <param name="NoiseMean">The mean best score of the unrelated queries.</param>
/// <param name="SuggestedMinScore">A threshold just above the noise ceiling.</param>
/// <param name="Saved">Whether the suggestion was stored in the index's configuration.</param>
/// <param name="Questions">With <c>--questions</c>: how many real questions were run.</param>
/// <param name="Missed">With <c>--questions</c>: questions whose expected file was not among the best 200 hits.</param>
/// <param name="AnswerFloor">With <c>--questions</c>: the 10th percentile score of the expected files.</param>
/// <param name="AnswersAtOrBelowCeiling">With <c>--questions</c>: expected files that scored no better than noise (found ones plus the missed).</param>
/// <param name="Warning">With <c>--questions</c>: set when the answers and the noise overlap, so no threshold separates them.</param>
internal sealed record CalibrationResult(
    string Index,
    int Probes,
    double NoiseCeiling,
    double NoiseMean,
    double SuggestedMinScore,
    bool Saved,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Questions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Missed = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? AnswerFloor = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? AnswersAtOrBelowCeiling = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Warning = null);

/// <summary>A real question and the file that answers it, for <c>calibrate --questions</c>.</summary>
/// <param name="Question">The question as a user would ask it.</param>
/// <param name="ExpectedPath">The answering file, absolute or relative to the index root.</param>
internal sealed record CalibrationQuestion(string Question, string ExpectedPath);

/// <summary>A line range read from an indexed file.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="StartLine">The 1-based first line returned.</param>
/// <param name="EndLine">The 1-based last line returned.</param>
/// <param name="TotalLines">The number of lines in the file.</param>
/// <param name="Stale">Whether the file changed since it was indexed, so line numbers from search may have drifted.</param>
/// <param name="Text">The requested lines.</param>
internal sealed record ReadResult(string Path, int StartLine, int EndLine, int TotalLines, bool Stale, string Text);

/// <summary>Result of a <c>drop</c> run.</summary>
internal sealed record DropResult(IndexStatus Status, string Index, int ChunksDeleted);


/// <summary>
/// The indexer's operations. Every index maps to a vector-store project owned by <see cref="UserId"/>; its
/// per-file manifest lives in <see cref="ManifestStore"/>.
/// </summary>
internal sealed class IndexService(
    IVectorStore vectorStore,
    ManifestStore manifest,
    ITextSplitter splitter,
    IWriterLock writerLock,
    string embeddingModel,
    PassageOptions? passage = null)
{
    private readonly PassageOptions _passage = passage ?? PassageOptions.Default;

    /// <summary>The store user every index belongs to, keeping indexer data apart from other Agency users.</summary>
    public const string UserId = "agency-index";

    /// <summary>The most lines one <see cref="ReadAsync"/> call returns.</summary>
    public const int MaxReadLines = 400;

    /// <summary>
    /// The session passed when searching. It never holds data; a non-null session restricts results to the
    /// requested projects on both backends (Postgres reads a null session as "every session and project").
    /// </summary>
    private const string SearchSession = "agency-index";

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The number of chunks embedded to time a dry run; the estimate scales their rate to every character.</summary>
    private const int DryRunSampleChunks = 48;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>How many times <c>top</c> candidates a path-filtered search retrieves before filtering.</summary>
    private const int PathFilterOverfetch = 10;

    /// <summary>How far above the noise ceiling a suggested threshold sits.</summary>
    private const double CalibrationMargin = 0.03;

    /// <summary>How many hits <c>calibrate --questions</c> looks through for each question's expected file.</summary>
    private const int AnswerPool = 200;

    /// <summary>The least distance between the answer floor and the noise ceiling for which a threshold is suggested.</summary>
    private const double MinAnswerGap = 0.02;

    /// <summary>Probe queries about nothing a documentation set would cover; the best score any of them reaches is the noise floor.</summary>
    private static readonly string[] CalibrationProbes =
    [
        "chocolate cake recipe with butter and eggs",
        "football match final score and league table",
        "how to knit a wool scarf",
        "weather forecast for tomorrow afternoon",
        "symptoms of the common cold and flu",
        "best beaches to visit in southern Italy",
        "history of the Roman Empire and its emperors",
        "how to train a puppy to sit",
        "stock market prices and interest rates",
        "planting tomatoes in a vegetable garden",
        "guitar chords for a folk song",
        "renewing a passport at the post office",
    ];

    /// <summary>Brings <see cref="IndexRequest.Index"/> up to date with the files on disk.</summary>
    /// <param name="request">What to index.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="progress">Receives a human-readable line at the start and after each file; the result is unaffected.</param>
    public async Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken ct, Action<string>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        await using IAsyncDisposable? held = await this.AcquireAsync(request.Index, request.Wait, ct);
        if (held is null)
        {
            return new IndexResult(IndexStatus.Locked, request.Index, request.Root, [], [], [], 0, [], [], 0, stopwatch.ElapsedMilliseconds);
        }

        IndexConfig? previous = await manifest.GetConfigAsync(request.Index, ct);
        IndexConfig config = await this.ResolveConfigAsync(request, ct);

        // Saved before any file is processed so the index is searchable (partially) while its first build runs.
        await manifest.SaveConfigAsync(request.Index, config, ct);
        ScanResult scan = FileScanner.Scan(new ScanOptions(config.Root, config.Extensions, config.Names, request.MaxFileBytes, config.Excludes));
        IReadOnlyList<ManifestEntry> entries = await manifest.GetEntriesAsync(request.Index, ct);
        IndexPlan plan = DeltaPlanner.Plan(scan.Files, entries);
        if (request.Rebuild && previous is not null)
        {
            // Forget what was indexed, so an interrupted rebuild is finished by a plain run instead of leaving old-model chunks
            // that look current. Chunks of files that no longer exist are removed below as usual.
            foreach (ManifestEntry entry in entries.Where(e => !plan.Removed.Contains(e.Path, StringComparer.Ordinal)))
            {
                await manifest.DeleteEntryAsync(request.Index, entry.Path, ct);
            }

            plan = new IndexPlan(scan.Files.ToList(), [], plan.Removed, 0);
        }

        var failed = new List<FailedFile>();
        int chunksWritten = 0;

        // Chunking is local and quick, so it happens up front: the progress lines can then say "N of M chunks".
        List<ScannedFile> toIndex = plan.Added.Concat(plan.Changed).ToList();
        var chunked = new List<(ScannedFile File, List<DocumentChunk<string>>? Chunks)>(toIndex.Count);
        foreach (ScannedFile file in toIndex)
        {
            try
            {
                chunked.Add((file, await this.ChunkFileAsync(file, config, ct)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string reason = FailureReason.Of(ex);
                failed.Add(new FailedFile(file.Path, reason));
                progress?.Invoke($"FAILED {file.Path}: {reason}");
                chunked.Add((file, null));
            }
        }

        int totalChunks = chunked.Sum(c => c.Chunks?.Count ?? 0);
        progress?.Invoke($"indexing {toIndex.Count} files ({plan.Added.Count} added, {plan.Changed.Count} changed), {totalChunks} chunks, removing {plan.Removed.Count}");

        int chunksDone = 0;
        string currentPath = "";

        // A file with many chunks can take minutes; say so periodically so a log tail does not look hung.
        using var heartbeat = new Timer(
            _ => progress?.Invoke($"still working on {Path.GetFileName(Volatile.Read(ref currentPath))}: {Volatile.Read(ref chunksDone)}/{totalChunks} chunks, {FormatDuration(stopwatch.Elapsed)} elapsed"),
            null,
            HeartbeatInterval,
            HeartbeatInterval);
        for (int i = 0; i < chunked.Count; i++)
        {
            (ScannedFile file, List<DocumentChunk<string>>? chunks) = chunked[i];
            Volatile.Write(ref currentPath, file.Path);
            if (chunks is not null)
            {
                try
                {
                    await vectorStore.ReplaceDocumentAsync(UserId, null, file.Path, chunks, request.Index, ct);
                    await manifest.SaveEntryAsync(request.Index, new ManifestEntry(file.Path, file.Size, file.LastWriteTicks, chunks.Count), ct);
                    chunksWritten += chunks.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable file or failed embedding call must not abort the rest of the run; the file
                    // keeps its old manifest entry (or none), so the next run retries it.
                    string reason = FailureReason.Of(ex);
                    failed.Add(new FailedFile(file.Path, reason));
                    progress?.Invoke($"FAILED {file.Path}: {reason}");
                }
            }

            Volatile.Write(ref chunksDone, chunksDone + (chunks?.Count ?? 0));
            progress?.Invoke(ProgressLine(i + 1, toIndex.Count, chunksWritten, chunksDone, totalChunks, failed.Count, stopwatch.Elapsed));
        }

        foreach (string path in plan.Removed)
        {
            await vectorStore.ReplaceDocumentAsync<string>(UserId, null, path, [], request.Index, ct);
            await manifest.DeleteEntryAsync(request.Index, path, ct);
        }

        if (failed.Count == 0 && (toIndex.Count > 0 || plan.Removed.Count > 0 || config.Noise is null))
        {
            await this.RefreshStatisticsAsync(request.Index, scan.Files, ct);
        }

        var failedPaths = failed.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return new IndexResult(
            failed.Count == 0 ? IndexStatus.Ok : IndexStatus.PartialFailure,
            request.Index,
            config.Root,
            plan.Added.Select(f => f.Path).Where(p => !failedPaths.Contains(p)).ToList(),
            plan.Changed.Select(f => f.Path).Where(p => !failedPaths.Contains(p)).ToList(),
            plan.Removed,
            plan.Unchanged,
            scan.SkippedTooLarge,
            failed,
            chunksWritten,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Reports what <see cref="IndexAsync"/> would do without writing anything: the delta, the chunk count, and a
    /// time estimate from embedding the chunks of the first few files (the embeddings are discarded). Takes no writer lock.
    /// </summary>
    public async Task<DryRunResult> DryRunAsync(IndexRequest request, IEmbeddingGenerator embeddings, CancellationToken ct)
    {
        IndexConfig config = await this.ResolveConfigAsync(request, ct);
        ScanResult scan = FileScanner.Scan(new ScanOptions(config.Root, config.Extensions, config.Names, request.MaxFileBytes, config.Excludes));
        IndexPlan plan = DeltaPlanner.Plan(scan.Files, await manifest.GetEntriesAsync(request.Index, ct));
        if (request.Rebuild)
        {
            plan = new IndexPlan(scan.Files.ToList(), [], plan.Removed, 0);
        }

        var allChunks = new List<string>();
        foreach (ScannedFile file in plan.Added.Concat(plan.Changed))
        {
            allChunks.AddRange((await this.ChunkFileAsync(file, config, ct)).Select(c => c.Value));
        }

        int estimatedChunks = allChunks.Count;

        // Time a sample spread across the whole set (the first files are often small stubs) and scale by text length, because
        // embedding time follows tokens rather than chunk count.
        double? seconds = null;
        if (allChunks.Count > 0)
        {
            int sampleSize = Math.Min(DryRunSampleChunks, allChunks.Count);
            List<string> sample = Enumerable.Range(0, sampleSize).Select(i => allChunks[(int)((long)i * allChunks.Count / sampleSize)]).ToList();
            var timer = Stopwatch.StartNew();
            await embeddings.GenerateEmbeddingsAsync(sample, ct);
            double sampleChars = Math.Max(1, sample.Sum(c => (double)c.Length));
            seconds = Math.Round(timer.Elapsed.TotalSeconds * allChunks.Sum(c => (double)c.Length) / sampleChars, 1);
        }

        return new DryRunResult(
            request.Index,
            config.Root,
            plan.Added.Select(f => f.Path).ToList(),
            plan.Changed.Select(f => f.Path).ToList(),
            plan.Removed,
            plan.Unchanged,
            scan.SkippedTooLarge,
            estimatedChunks,
            seconds);
    }

    /// <summary>
    /// Returns the chunks of <paramref name="index"/> closest to <paramref name="text"/>, best first: the nearest
    /// <paramref name="top"/>, or the nearest <paramref name="pool"/> when that is larger (so the caller can filter, group and
    /// cut them afterwards). <paramref name="pathGlob"/> keeps only files whose path under the index root matches it.
    /// </summary>
    public async Task<IReadOnlyList<SearchResultHit>> SearchAsync(
        string index,
        string text,
        int top,
        CancellationToken ct,
        string? pathGlob = null,
        int pool = 0)
    {
        IndexConfig config = await manifest.GetConfigAsync(index, ct) ?? throw new UsageException($"Index '{index}' does not exist. Run 'agency-index index --index {index} --root <dir>' first.");
        if (!string.Equals(config.EmbeddingModel, embeddingModel, StringComparison.Ordinal))
        {
            throw new UsageException($"Index '{index}' was built with embedding model '{config.EmbeddingModel}' but '{embeddingModel}' is configured, so its scores are meaningless. Run 'agency-index index --index {index} --rebuild' to re-embed it, or configure the original model.");
        }

        // A path filter removes hits after the search, so ask for more than will be kept.
        int candidates = Math.Max(top, pool);
        if (pathGlob is not null)
        {
            candidates = Math.Max(candidates, top * PathFilterOverfetch);
        }

        IReadOnlyList<SearchHit<string>> hits = await vectorStore.SearchAsync<string>(
            new Query(UserId, SearchSession, null, text, null, candidates, true, [index]), ct);

        IReadOnlyList<SearchResultHit> results = hits.Select(h => new SearchResultHit(
            h.Metadata?.GetValueOrDefault("source_file") as string ?? h.Key,
            h.Metadata?.GetValueOrDefault("chunk_index") as long?,
            Math.Round(Math.Max(0, 1.0 - h.Distance), 4),
            h.Value,
            h.Metadata?.GetValueOrDefault("heading") as string,
            h.Metadata?.GetValueOrDefault("start_line") as long?,
            h.Metadata?.GetValueOrDefault("end_line") as long?,
            index)).ToList();

        if (pathGlob is not null)
        {
            var filter = new GlobFilter([pathGlob]);
            results = results.Where(r => filter.Matches(Path.GetRelativePath(config.Root, r.Path))).ToList();
        }

        return pool > 0 ? results : results.Take(top).ToList();
    }

    /// <summary>
    /// Runs unrelated queries against <paramref name="index"/> and reports the best score any of them reached: the noise ceiling
    /// for this model and corpus. A threshold just above it keeps on-topic hits and drops noise.
    /// </summary>
    /// <param name="index">The index to probe.</param>
    /// <param name="save">Whether to store the suggested threshold so <c>search</c> uses it when none is configured.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="questions">Real questions with their expected files, to measure the answer floor against the noise ceiling.</param>
    public async Task<CalibrationResult> CalibrateAsync(string index, bool save, CancellationToken ct, IReadOnlyList<CalibrationQuestion>? questions = null)
    {
        NoiseStats noise = await this.MeasureNoiseAsync(index, ct) ?? throw new UsageException($"Index '{index}' has no chunks to calibrate against.");
        double ceiling = noise.Ceiling;
        double suggested = Math.Min(0.99, Math.Round(ceiling + CalibrationMargin, 2));
        int? missed = null;
        double? floor = null;
        int? atOrBelow = null;
        string? warning = null;
        if (questions is { Count: > 0 })
        {
            IndexConfig current = (await manifest.GetConfigAsync(index, ct))!;
            var found = new List<double>();
            int misses = 0;
            foreach (CalibrationQuestion question in questions)
            {
                string expected = Path.GetFullPath(question.ExpectedPath, current.Root);
                IReadOnlyList<SearchResultHit> hits = await this.SearchAsync(index, question.Question, AnswerPool, ct);
                SearchResultHit? hit = hits.FirstOrDefault(h => string.Equals(h.Path, expected, StringComparison.Ordinal));
                if (hit is null)
                {
                    misses++;
                }
                else
                {
                    found.Add(hit.Score);
                }
            }

            missed = misses;
            if (found.Count > 0)
            {
                found.Sort();
                floor = found[(int)Math.Floor(0.1 * (found.Count - 1))];
            }

            atOrBelow = found.Count(s => s <= ceiling) + misses;
            if (floor is { } f && f - ceiling >= MinAnswerGap && misses == 0)
            {
                suggested = Math.Round(ceiling + ((f - ceiling) / 2), 3);
            }
            else
            {
                warning = floor is null
                    ? "None of the expected files was among the best hits, so no threshold can be suggested."
                    : $"The answer floor {floor:0.000} is within {MinAnswerGap:0.00} of the noise ceiling {ceiling:0.000}{(misses > 0 ? $" and {misses} expected file(s) were not retrieved" : "")}: any minimum score either keeps noise or hides real answers. Do not set Search:MinScore; use the normalized score and the no-match line instead.";
            }
        }

        bool canSave = save && warning is null;
        if (save)
        {
            IndexConfig config = (await manifest.GetConfigAsync(index, ct))!;
            await manifest.SaveConfigAsync(index, config with { Noise = noise, Calibration = canSave ? new Calibration(ceiling, suggested, floor) : config.Calibration }, ct);
        }

        return new CalibrationResult(index, CalibrationProbes.Length, ceiling, Math.Round(noise.Mean, 4), suggested, canSave, questions is { Count: > 0 } ? questions.Count : null, missed, floor, atOrBelow, warning);
    }

    /// <summary>Runs the unrelated probe queries and returns the distribution of their best scores, or <see langword="null"/> for an empty index.</summary>
    public async Task<NoiseStats?> MeasureNoiseAsync(string index, CancellationToken ct)
    {
        var bestScores = new List<double>();
        foreach (string probe in CalibrationProbes)
        {
            IReadOnlyList<SearchResultHit> hits = await this.SearchAsync(index, probe, 1, ct);
            if (hits.Count > 0)
            {
                bestScores.Add(hits[0].Score);
            }
        }

        if (bestScores.Count == 0)
        {
            return null;
        }

        double mean = bestScores.Average();
        double deviation = Math.Sqrt(bestScores.Sum(s => (s - mean) * (s - mean)) / bestScores.Count);
        return new NoiseStats(Math.Round(mean, 4), Math.Round(deviation, 4), bestScores.Max());
    }

    /// <summary>Returns the configuration of <paramref name="index"/>.</summary>
    public async Task<IndexConfig> GetConfigAsync(string index, CancellationToken ct) =>
        await manifest.GetConfigAsync(index, ct) ?? throw new UsageException($"Index '{index}' does not exist. Run 'agency-index index --index {index} --root <dir>' first.");

    /// <summary>
    /// After an index run: measures the noise distribution (so scores can be normalized) and the words common to most files (a project
    /// name the query need not carry). A failure here leaves the index usable, only without those two aids.
    /// </summary>
    private async Task RefreshStatisticsAsync(string index, IEnumerable<ScannedFile> files, CancellationToken ct)
    {
        try
        {
            IndexConfig config = (await manifest.GetConfigAsync(index, ct))!;
            NoiseStats? noise = await this.MeasureNoiseAsync(index, ct);
            IReadOnlyList<string> common = Lexical.CommonTerms(files.Select(f => ReadPlainText(f.Path)).Where(t => t is not null)!);
            await manifest.SaveConfigAsync(index, config with { Noise = noise, CommonTerms = common }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Statistics are an aid; an embedding hiccup here must not fail an index run that otherwise succeeded.
        }
    }

    private static string? ReadPlainText(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            return Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" ? HtmlTextExtractor.Extract(text) : text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Returns the threshold stored by <see cref="CalibrateAsync"/> for <paramref name="index"/>, or <see langword="null"/>.</summary>
    public async Task<double?> GetSuggestedMinScoreAsync(string index, CancellationToken ct) =>
        (await manifest.GetConfigAsync(index, ct))?.Calibration?.SuggestedMinScore;

    /// <summary>
    /// Returns lines <paramref name="start"/> to <paramref name="end"/> (at most <see cref="MaxReadLines"/>) of
    /// <paramref name="path"/>, which must be a file of <paramref name="index"/>, absolute or relative to its root.
    /// </summary>
    public async Task<ReadResult> ReadAsync(string index, string path, int start, int? end, CancellationToken ct, bool all = false)
    {
        IndexConfig config = await manifest.GetConfigAsync(index, ct) ?? throw new UsageException($"Index '{index}' does not exist.");
        string full = Path.GetFullPath(path, config.Root);
        ManifestEntry entry = (await manifest.GetEntriesAsync(index, ct)).FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.Ordinal))
            ?? throw new UsageException($"'{path}' is not a file of index '{index}'. Use a path from search or list.");

        string[] lines = (await File.ReadAllTextAsync(full, ct)).ReplaceLineEndings("\n").Split('\n');
        int last = Math.Min(Math.Min(end ?? int.MaxValue, all ? int.MaxValue : start + MaxReadLines - 1), lines.Length);
        if (start > lines.Length || last < start)
        {
            throw new UsageException($"Line range {start}-{end} is outside '{path}', which has {lines.Length} lines.");
        }

        return new ReadResult(
            full,
            start,
            last,
            lines.Length,
            new FileInfo(full).LastWriteTimeUtc.Ticks != entry.LastWriteTicks,
            string.Join('\n', lines[(start - 1)..last]));
    }

    /// <summary>Returns the configuration and manifest of <paramref name="index"/>.</summary>
    public async Task<(IndexConfig Config, IReadOnlyList<ManifestEntry> Files)> ListAsync(string index, CancellationToken ct)
    {
        IndexConfig config = await manifest.GetConfigAsync(index, ct) ?? throw new UsageException($"Index '{index}' does not exist.");
        return (config, await manifest.GetEntriesAsync(index, ct));
    }

    /// <summary>Returns every index and its configuration.</summary>
    public Task<IReadOnlyList<(string Index, IndexConfig Config)>> ListIndexesAsync(CancellationToken ct) =>
        manifest.GetAllConfigsAsync(ct);

    /// <summary>Deletes every chunk and the manifest of <paramref name="index"/>.</summary>
    public async Task<DropResult> DropAsync(string index, bool wait, CancellationToken ct)
    {
        await using IAsyncDisposable? held = await this.AcquireAsync(index, wait, ct);
        if (held is null)
        {
            return new DropResult(IndexStatus.Locked, index, 0);
        }

        int deleted = await vectorStore.DeleteProjectAsync(UserId, index, ct);
        await manifest.DeleteIndexAsync(index, ct);
        return new DropResult(IndexStatus.Ok, index, deleted);
    }

    private async Task<IAsyncDisposable?> AcquireAsync(string index, bool wait, CancellationToken ct)
    {
        while (true)
        {
            IAsyncDisposable? held = await writerLock.TryAcquireAsync(index, ct);
            if (held is not null || !wait)
            {
                return held;
            }

            await Task.Delay(LockPollInterval, ct);
        }
    }

    /// <summary>
    /// Merges the request with the stored configuration. The root is fixed on the first run; the extension and
    /// name selection may change between runs (files no longer selected are removed). The embedding model must
    /// not change, because vectors from different models are not comparable.
    /// </summary>
    private async Task<IndexConfig> ResolveConfigAsync(IndexRequest request, CancellationToken ct)
    {
        IndexConfig? stored = await manifest.GetConfigAsync(request.Index, ct);
        string? root = request.Root is null ? null : Path.GetFullPath(request.Root);

        if (stored is null && root is null)
        {
            throw new UsageException($"Index '{request.Index}' does not exist yet; pass --root <dir> to create it.");
        }

        if (stored is not null && root is not null && !string.Equals(stored.Root, root, StringComparison.Ordinal))
        {
            throw new UsageException($"Index '{request.Index}' is bound to root '{stored.Root}'. Use another index name, or drop this one first.");
        }

        if (stored is not null && !request.Rebuild && !string.Equals(stored.EmbeddingModel, embeddingModel, StringComparison.Ordinal))
        {
            throw new UsageException($"Index '{request.Index}' was built with embedding model '{stored.EmbeddingModel}' but '{embeddingModel}' is configured. Run 'agency-index index --index {request.Index} --rebuild' to re-embed it with the configured model.");
        }

        if (request.Rebuild && stored is null)
        {
            throw new UsageException($"Index '{request.Index}' does not exist, so there is nothing to rebuild; run it without --rebuild.");
        }

        root ??= stored!.Root;
        if (!Directory.Exists(root))
        {
            throw new UsageException($"Root directory '{root}' does not exist.");
        }

        // An index keeps the format it was built in until it is rebuilt; passage size is part of the format.
        int format = stored is null || request.Rebuild ? IndexFormat.Current : stored.FormatVersion;
        PassageOptions passageShape = format >= IndexFormat.Passages ? this._passage : new PassageOptions(0, 0);
        if (stored is { FormatVersion: >= IndexFormat.Passages } && !request.Rebuild && (stored.PassageLines != passageShape.Lines || stored.PassageOverlap != passageShape.Overlap))
        {
            throw new UsageException($"Index '{request.Index}' was built with passages of {stored.PassageLines} lines (overlap {stored.PassageOverlap}) but {passageShape.Lines} (overlap {passageShape.Overlap}) is configured. Run 'agency-index index --index {request.Index} --rebuild' to re-cut it, or configure the original size.");
        }

        bool sameModel = stored is not null && string.Equals(stored.EmbeddingModel, embeddingModel, StringComparison.Ordinal);
        return new IndexConfig(
            root,
            request.Extensions ?? stored?.Extensions ?? FileScanner.DefaultExtensions,
            request.Names ?? stored?.Names ?? FileScanner.DefaultNames,
            embeddingModel,
            request.Exclude ?? stored?.Excludes,
            sameModel ? stored!.Calibration : null,
            format,
            passageShape.Lines,
            passageShape.Overlap,
            sameModel && !request.Rebuild ? stored!.Noise : null,
            sameModel && !request.Rebuild ? stored!.CommonTerms : null);
    }

    /// <summary>Reads <paramref name="file"/> and splits it into the chunks that would be embedded.</summary>
    private async Task<List<DocumentChunk<string>>> ChunkFileAsync(ScannedFile file, IndexConfig config, CancellationToken ct)
    {
        string extension = Path.GetExtension(file.Path).ToLowerInvariant();
        string content = await File.ReadAllTextAsync(file.Path, ct);
        if (extension is ".html" or ".htm")
        {
            content = HtmlTextExtractor.Extract(content);
        }

        if (config.FormatVersion >= IndexFormat.Passages && extension is not ".html" and not ".htm")
        {
            return PassageChunks(file, content, extension, new PassageOptions(config.PassageLines, config.PassageOverlap));
        }

        var document = new Document(content, file.Path, new Dictionary<string, object>
        {
            ["file_path"] = file.Path,
            ["file_name"] = Path.GetFileName(file.Path),
            ["file_extension"] = extension,
        });

        List<Document> pieces = splitter.Split(document).ToList();

        // Line numbers refer to the text as stored, which for HTML is the extracted text rather than the source markup.
        IReadOnlyList<ChunkLocation> locations = extension is ".html" or ".htm"
            ? []
            : ChunkLocator.Locate(content, pieces.Select(p => p.Content).ToList(), extension is ".md" or ".markdown" or ".mdx");

        return pieces
            .Select((chunk, i) =>
            {
                var metadata = new Dictionary<string, object>(chunk.Metadata ?? [], StringComparer.Ordinal) { ["chunk_index"] = i };
                if (i < locations.Count)
                {
                    ChunkLocation where = locations[i];
                    if (where.Heading is not null)
                    {
                        metadata["heading"] = where.Heading;
                    }

                    if (where.StartLine is { } start && where.EndLine is { } end)
                    {
                        metadata["start_line"] = (long)start;
                        metadata["end_line"] = (long)end;
                    }
                }

                return new DocumentChunk<string>($"{file.Path}:chunk:{i}", chunk.Content, metadata);
            })
            .ToList();
    }

    /// <summary>The passages of a file as vector-store chunks: each is embedded with its heading path and records its exact lines.</summary>
    private static List<DocumentChunk<string>> PassageChunks(ScannedFile file, string content, string extension, PassageOptions shape) =>
        PassageSplitter.Split(content, extension is ".md" or ".markdown" or ".mdx", shape)
            .Select((p, i) =>
            {
                var metadata = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["file_path"] = file.Path,
                    ["file_name"] = Path.GetFileName(file.Path),
                    ["file_extension"] = extension,
                    ["chunk_index"] = i,
                    ["start_line"] = (long)p.StartLine,
                    ["end_line"] = (long)p.EndLine,
                };
                if (p.Heading is not null)
                {
                    metadata["heading"] = p.Heading;
                }

                string value = p.Embedded.Length > PassageSplitter.MaxEmbeddedChars ? p.Embedded[..PassageSplitter.MaxEmbeddedChars] : p.Embedded;
                return new DocumentChunk<string>($"{file.Path}:chunk:{i}", value, metadata);
            })
            .ToList();

    /// <summary>E.g. <c>indexing 12/340 files, 410/1980 chunks, 1 failed, ~6 min left</c>; the estimate follows the chunks still to do.</summary>
    private static string ProgressLine(int done, int total, int chunksWritten, int chunksDone, int totalChunks, int failedFiles, TimeSpan elapsed)
    {
        string failures = failedFiles > 0 ? $", {failedFiles} failed" : "";
        if (done == total)
        {
            return $"indexing {done}/{total} files, {chunksWritten} chunks{failures}, done in {FormatDuration(elapsed)}";
        }

        double remaining = chunksDone > 0 ? (double)(totalChunks - chunksDone) / chunksDone : (double)(total - done) / done;
        return $"indexing {done}/{total} files, {chunksDone}/{totalChunks} chunks{failures}, ~{FormatDuration(elapsed * remaining)} left";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 90 ? $"{Math.Max(1, (int)duration.TotalSeconds)}s" : $"{(int)Math.Round(duration.TotalMinutes)} min";
}
