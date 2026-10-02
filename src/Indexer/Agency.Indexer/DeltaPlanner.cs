namespace Agency.Indexer;

/// <summary>A file found on disk by <see cref="FileScanner"/>.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="LastWriteTicks">The last-write time as UTC ticks.</param>
internal sealed record ScannedFile(string Path, long Size, long LastWriteTicks);

/// <summary>What the manifest recorded for a file the last time it was indexed.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Size">The file size in bytes when it was indexed.</param>
/// <param name="LastWriteTicks">The last-write time, as UTC ticks, when it was indexed.</param>
/// <param name="Chunks">The number of chunks stored for the file.</param>
internal sealed record ManifestEntry(string Path, long Size, long LastWriteTicks, int Chunks);

/// <summary>The file-level delta between the disk and the manifest.</summary>
/// <param name="Added">Files on disk that the manifest does not know.</param>
/// <param name="Changed">Files whose size or last-write time differs from the manifest.</param>
/// <param name="Removed">Manifest paths no longer on disk (or no longer selected).</param>
/// <param name="Unchanged">The number of files whose size and last-write time match the manifest.</param>
internal sealed record IndexPlan(
    IReadOnlyList<ScannedFile> Added,
    IReadOnlyList<ScannedFile> Changed,
    IReadOnlyList<string> Removed,
    int Unchanged);

/// <summary>
/// Computes which files need indexing by comparing size and last-write time; file contents are never read.
/// </summary>
internal static class DeltaPlanner
{
    /// <summary>Compares <paramref name="disk"/> against <paramref name="manifest"/>.</summary>
    /// <param name="disk">The files currently selected on disk.</param>
    /// <param name="manifest">The files recorded by the previous index run.</param>
    public static IndexPlan Plan(IReadOnlyList<ScannedFile> disk, IReadOnlyList<ManifestEntry> manifest)
    {
        var known = manifest.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var added = new List<ScannedFile>();
        var changed = new List<ScannedFile>();
        int unchanged = 0;

        foreach (ScannedFile file in disk)
        {
            if (!known.TryGetValue(file.Path, out ManifestEntry? entry))
            {
                added.Add(file);
            }
            else if (entry.Size != file.Size || entry.LastWriteTicks != file.LastWriteTicks)
            {
                changed.Add(file);
            }
            else
            {
                unchanged++;
            }
        }

        var onDisk = disk.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        List<string> removed = manifest.Select(e => e.Path).Where(p => !onDisk.Contains(p)).Order(StringComparer.Ordinal).ToList();

        return new IndexPlan(added, changed, removed, unchanged);
    }
}
