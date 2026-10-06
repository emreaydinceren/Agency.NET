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
internal sealed record IndexRequest(string Index, string? Root, IReadOnlyList<string>? Extensions, IReadOnlyList<string>? Names, long MaxFileBytes, bool Wait);

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
internal sealed record SearchResultHit(string Path, long? Chunk, double Score, string Text);

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
    string embeddingModel)
{
    /// <summary>The store user every index belongs to, keeping indexer data apart from other Agency users.</summary>
    public const string UserId = "agency-index";

    /// <summary>
    /// The session passed when searching. It never holds data; a non-null session restricts results to the
    /// requested projects on both backends (Postgres reads a null session as "every session and project").
    /// </summary>
    private const string SearchSession = "agency-index";

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The number of files embedded to time a dry run; the estimate scales their rate to every chunk.</summary>
    private const int DryRunSampleFiles = 3;

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

        IndexConfig config = await this.ResolveConfigAsync(request, ct);

        // Saved before any file is processed so the index is searchable (partially) while its first build runs.
        await manifest.SaveConfigAsync(request.Index, config, ct);
        ScanResult scan = FileScanner.Scan(new ScanOptions(config.Root, config.Extensions, config.Names, request.MaxFileBytes));
        IndexPlan plan = DeltaPlanner.Plan(scan.Files, await manifest.GetEntriesAsync(request.Index, ct));

        var failed = new List<FailedFile>();
        int chunksWritten = 0;

        List<ScannedFile> toIndex = plan.Added.Concat(plan.Changed).ToList();
        progress?.Invoke($"indexing {toIndex.Count} files ({plan.Added.Count} added, {plan.Changed.Count} changed), removing {plan.Removed.Count}");
        for (int i = 0; i < toIndex.Count; i++)
        {
            ScannedFile file = toIndex[i];
            try
            {
                chunksWritten += await this.IndexFileAsync(request.Index, file, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable file or failed embedding call must not abort the rest of the run; the file
                // keeps its old manifest entry (or none), so the next run retries it.
                string reason = FailureReason.Of(ex);
                failed.Add(new FailedFile(file.Path, reason));
                progress?.Invoke($"FAILED {file.Path}: {reason}");
            }

            progress?.Invoke(ProgressLine(i + 1, toIndex.Count, chunksWritten, failed.Count, stopwatch.Elapsed));
        }

        foreach (string path in plan.Removed)
        {
            await vectorStore.ReplaceDocumentAsync<string>(UserId, null, path, [], request.Index, ct);
            await manifest.DeleteEntryAsync(request.Index, path, ct);
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
        ScanResult scan = FileScanner.Scan(new ScanOptions(config.Root, config.Extensions, config.Names, request.MaxFileBytes));
        IndexPlan plan = DeltaPlanner.Plan(scan.Files, await manifest.GetEntriesAsync(request.Index, ct));

        int estimatedChunks = 0;
        var sample = new List<string>();
        int sampledFiles = 0;
        foreach (ScannedFile file in plan.Added.Concat(plan.Changed))
        {
            List<DocumentChunk<string>> chunks = await this.ChunkFileAsync(file, ct);
            estimatedChunks += chunks.Count;
            if (sampledFiles++ < DryRunSampleFiles)
            {
                sample.AddRange(chunks.Select(c => c.Value));
            }
        }

        double? seconds = null;
        if (sample.Count > 0)
        {
            var timer = Stopwatch.StartNew();
            await embeddings.GenerateEmbeddingsAsync(sample, ct);
            seconds = Math.Round(timer.Elapsed.TotalSeconds * estimatedChunks / sample.Count, 1);
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

    /// <summary>Returns the <paramref name="top"/> chunks of <paramref name="index"/> closest to <paramref name="text"/>.</summary>
    public async Task<IReadOnlyList<SearchResultHit>> SearchAsync(string index, string text, int top, CancellationToken ct)
    {
        _ = await manifest.GetConfigAsync(index, ct) ?? throw new UsageException($"Index '{index}' does not exist. Run 'agency-index index --index {index} --root <dir>' first.");

        IReadOnlyList<SearchHit<string>> hits = await vectorStore.SearchAsync<string>(
            new Query(UserId, SearchSession, null, text, null, top, true, [index]), ct);

        return hits.Select(h => new SearchResultHit(
            h.Metadata?.GetValueOrDefault("source_file") as string ?? h.Key,
            h.Metadata?.GetValueOrDefault("chunk_index") as long?,
            Math.Round(Math.Max(0, 1.0 - h.Distance), 4),
            h.Value)).ToList();
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

        if (stored is not null && !string.Equals(stored.EmbeddingModel, embeddingModel, StringComparison.Ordinal))
        {
            throw new UsageException($"Index '{request.Index}' was built with embedding model '{stored.EmbeddingModel}' but '{embeddingModel}' is configured. Drop and rebuild the index to switch models.");
        }

        root ??= stored!.Root;
        if (!Directory.Exists(root))
        {
            throw new UsageException($"Root directory '{root}' does not exist.");
        }

        return new IndexConfig(
            root,
            request.Extensions ?? stored?.Extensions ?? FileScanner.DefaultExtensions,
            request.Names ?? stored?.Names ?? FileScanner.DefaultNames,
            embeddingModel);
    }

    /// <summary>Re-chunks and re-embeds one whole file, then records it in the manifest.</summary>
    /// <returns>The number of chunks written.</returns>
    private async Task<int> IndexFileAsync(string index, ScannedFile file, CancellationToken ct)
    {
        List<DocumentChunk<string>> chunks = await this.ChunkFileAsync(file, ct);
        await vectorStore.ReplaceDocumentAsync(UserId, null, file.Path, chunks, index, ct);
        await manifest.SaveEntryAsync(index, new ManifestEntry(file.Path, file.Size, file.LastWriteTicks, chunks.Count), ct);
        return chunks.Count;
    }

    /// <summary>Reads <paramref name="file"/> and splits it into the chunks that would be embedded.</summary>
    private async Task<List<DocumentChunk<string>>> ChunkFileAsync(ScannedFile file, CancellationToken ct)
    {
        string extension = Path.GetExtension(file.Path).ToLowerInvariant();
        string content = await File.ReadAllTextAsync(file.Path, ct);
        if (extension is ".html" or ".htm")
        {
            content = HtmlTextExtractor.Extract(content);
        }

        var document = new Document(content, file.Path, new Dictionary<string, object>
        {
            ["file_path"] = file.Path,
            ["file_name"] = Path.GetFileName(file.Path),
            ["file_extension"] = extension,
        });

        return splitter.Split(document)
            .Select((chunk, i) => new DocumentChunk<string>(
                $"{file.Path}:chunk:{i}",
                chunk.Content,
                new Dictionary<string, object>(chunk.Metadata ?? [], StringComparer.Ordinal) { ["chunk_index"] = i }))
            .ToList();
    }

    /// <summary>E.g. <c>indexing 12/340 files, 410 chunks, 1 failed, ~6 min left</c>.</summary>
    private static string ProgressLine(int done, int total, int chunks, int failedFiles, TimeSpan elapsed)
    {
        string failures = failedFiles > 0 ? $", {failedFiles} failed" : "";
        if (done == total)
        {
            return $"indexing {done}/{total} files, {chunks} chunks{failures}, done in {FormatDuration(elapsed)}";
        }

        return $"indexing {done}/{total} files, {chunks} chunks{failures}, ~{FormatDuration(elapsed * (total - done) / done)} left";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 90 ? $"{Math.Max(1, (int)duration.TotalSeconds)}s" : $"{(int)Math.Round(duration.TotalMinutes)} min";
}
