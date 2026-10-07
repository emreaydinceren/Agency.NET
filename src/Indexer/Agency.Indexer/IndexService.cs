using System.Diagnostics;
using System.Text.Json.Serialization;
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
/// <param name="Error">Why it failed.</param>
internal sealed record FailedFile(string Path, string Error);

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

/// <summary>One semantic search hit.</summary>
/// <param name="Path">The full path of the source file.</param>
/// <param name="Chunk">The chunk index within the file.</param>
/// <param name="Score">Cosine similarity in [0, 1]; higher is closer.</param>
/// <param name="Heading">The nearest Markdown heading above the chunk, when known.</param>
/// <param name="StartLine">The 1-based first line of the chunk in the file, when known.</param>
/// <param name="EndLine">The 1-based last line of the chunk in the file, when known.</param>
/// <param name="Text">The chunk text; <see langword="null"/> in compact output.</param>
internal sealed record SearchResultHit(
    string Path,
    long? Chunk,
    double Score,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Heading,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StartLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? EndLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text);

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
    string embeddingModel)
{
    /// <summary>The store user every index belongs to, keeping indexer data apart from other Agency users.</summary>
    public const string UserId = "agency-index";

    /// <summary>
    /// The session passed when searching. It never holds data; a non-null session restricts results to the
    /// requested projects on both backends (Postgres reads a null session as "every session and project").
    /// </summary>
    private const string SearchSession = "agency-index";

    /// <summary>The most lines one <see cref="ReadAsync"/> call returns.</summary>
    public const int MaxReadLines = 400;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Brings <see cref="IndexRequest.Index"/> up to date with the files on disk.</summary>
    public async Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken ct)
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

        foreach (ScannedFile file in plan.Added.Concat(plan.Changed))
        {
            try
            {
                chunksWritten += await this.IndexFileAsync(request.Index, file, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable file or failed embedding call must not abort the rest of the run; the file
                // keeps its old manifest entry (or none), so the next run retries it.
                failed.Add(new FailedFile(file.Path, ex.Message));
            }
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
            h.Metadata?.GetValueOrDefault("heading") as string,
            h.Metadata?.GetValueOrDefault("start_line") as long?,
            h.Metadata?.GetValueOrDefault("end_line") as long?,
            h.Value)).ToList();
    }

    /// <summary>
    /// Returns lines <paramref name="start"/> to <paramref name="end"/> (at most <see cref="MaxReadLines"/>) of
    /// <paramref name="path"/>, which must be a file of <paramref name="index"/>.
    /// </summary>
    public async Task<ReadResult> ReadAsync(string index, string path, int start, int? end, CancellationToken ct)
    {
        IndexConfig config = await manifest.GetConfigAsync(index, ct) ?? throw new UsageException($"Index '{index}' does not exist.");
        string full = Path.GetFullPath(path, config.Root);
        ManifestEntry entry = (await manifest.GetEntriesAsync(index, ct)).FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.Ordinal))
            ?? throw new UsageException($"'{path}' is not a file of index '{index}'. Use a path from search or list.");

        string[] lines = (await File.ReadAllTextAsync(full, ct)).ReplaceLineEndings("\n").Split('\n');
        int last = Math.Min(Math.Min(end ?? int.MaxValue, start + MaxReadLines - 1), lines.Length);
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

        List<Document> pieces = splitter.Split(document).ToList();

        // HTML is searched as extracted text, so its line numbers would not match the file on disk.
        IReadOnlyList<ChunkSpan> spans = extension is ".html" or ".htm"
            ? pieces.Select(_ => new ChunkSpan(null, null, null)).ToList()
            : ChunkLocator.Locate(content, pieces.Select(p => p.Content).ToList(), extension is ".md" or ".markdown" or ".mdx");

        List<DocumentChunk<string>> chunks = pieces
            .Select((chunk, i) =>
            {
                var metadata = new Dictionary<string, object>(chunk.Metadata ?? [], StringComparer.Ordinal) { ["chunk_index"] = i };
                if (spans[i].Heading is { } heading)
                {
                    metadata["heading"] = heading;
                }

                if (spans[i].StartLine is { } startLine && spans[i].EndLine is { } endLine)
                {
                    metadata["start_line"] = startLine;
                    metadata["end_line"] = endLine;
                }

                return new DocumentChunk<string>($"{file.Path}:chunk:{i}", chunk.Content, metadata);
            })
            .ToList();

        await vectorStore.ReplaceDocumentAsync(UserId, null, file.Path, chunks, index, ct);
        await manifest.SaveEntryAsync(index, new ManifestEntry(file.Path, file.Size, file.LastWriteTicks, chunks.Count), ct);
        return chunks.Count;
    }
}
