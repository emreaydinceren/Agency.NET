namespace Agency.Indexer;

/// <summary>
/// Shapes the results of <c>index</c>, <c>index --dry-run</c> and <c>list</c> for the calling agent: file paths relative to the
/// index root (the root is in the result) so a hundred files are not a hundred absolute paths, and, with <c>--summary</c>, counts instead
/// of lists.
/// </summary>
internal static class IndexOutput
{
    /// <summary>The paths of <paramref name="paths"/> relative to <paramref name="root"/>, with <c>/</c> separators.</summary>
    public static IReadOnlyList<string> Relative(string root, IEnumerable<string> paths) =>
        paths.Select(p => ToForwardSlashes(Path.GetRelativePath(root, p))).ToList();

    private static string ToForwardSlashes(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The printed form of an <c>index</c> run.</summary>
    public static object Of(IndexResult result, bool summary)
    {
        string root = result.Root ?? "";
        if (summary)
        {
            return new
            {
                status = result.Status,
                index = result.Index,
                root = result.Root,
                added = result.Added.Count,
                changed = result.Changed.Count,
                removed = result.Removed.Count,
                unchanged = result.Unchanged,
                skipped_too_large = result.SkippedTooLarge.Count,
                failed = result.Failed,
                chunks_written = result.ChunksWritten,
                duration_ms = result.DurationMs,
            };
        }

        return new
        {
            status = result.Status,
            index = result.Index,
            root = result.Root,
            added = Relative(root, result.Added),
            changed = Relative(root, result.Changed),
            removed = Relative(root, result.Removed),
            unchanged = result.Unchanged,
            skipped_too_large = Relative(root, result.SkippedTooLarge),
            failed = result.Failed,
            chunks_written = result.ChunksWritten,
            duration_ms = result.DurationMs,
        };
    }

    /// <summary>The printed form of a dry run.</summary>
    public static object Of(DryRunResult plan, bool summary) => summary
        ? new
        {
            index = plan.Index,
            root = plan.Root,
            added = plan.Added.Count,
            changed = plan.Changed.Count,
            removed = plan.Removed.Count,
            unchanged = plan.Unchanged,
            skipped_too_large = plan.SkippedTooLarge.Count,
            estimated_chunks = plan.EstimatedChunks,
            estimated_seconds = plan.EstimatedSeconds,
        }
        : new
        {
            index = plan.Index,
            root = plan.Root,
            added = Relative(plan.Root, plan.Added),
            changed = Relative(plan.Root, plan.Changed),
            removed = Relative(plan.Root, plan.Removed),
            unchanged = plan.Unchanged,
            skipped_too_large = Relative(plan.Root, plan.SkippedTooLarge),
            estimated_chunks = plan.EstimatedChunks,
            estimated_seconds = plan.EstimatedSeconds,
        };

    /// <summary>The printed form of <c>list</c>.</summary>
    public static object Of(string index, IndexConfig config, IReadOnlyList<ManifestEntry> files, bool summary) => summary
        ? new
        {
            status = "ok",
            index,
            config,
            file_count = files.Count,
            chunk_count = files.Sum(f => f.Chunks),
        }
        : new
        {
            status = "ok",
            index,
            config,
            files = files.Select(f => new
            {
                path = ToForwardSlashes(Path.GetRelativePath(config.Root, f.Path)),
                size = f.Size,
                last_write_utc = new DateTime(f.LastWriteTicks, DateTimeKind.Utc),
                chunks = f.Chunks,
            }),
        };
}
