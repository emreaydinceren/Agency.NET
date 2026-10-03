namespace Agency.Indexer;

/// <summary>Selects which files under a root are indexed.</summary>
/// <param name="Root">The full path of the directory to scan recursively.</param>
/// <param name="Extensions">File extensions to include, with a leading dot (matched case-insensitively).</param>
/// <param name="Names">Exact file names to include regardless of extension, e.g. <c>README</c> (matched case-insensitively).</param>
/// <param name="MaxFileBytes">Files larger than this are skipped.</param>
internal sealed record ScanOptions(string Root, IReadOnlyCollection<string> Extensions, IReadOnlyCollection<string> Names, long MaxFileBytes);

/// <summary>The outcome of a scan.</summary>
/// <param name="Files">The selected files, ordered by path.</param>
/// <param name="SkippedTooLarge">Paths that matched the selection but exceed the size cap.</param>
internal sealed record ScanResult(IReadOnlyList<ScannedFile> Files, IReadOnlyList<string> SkippedTooLarge);

/// <summary>Recursively finds the files to index, skipping build-output and dependency folders.</summary>
internal static class FileScanner
{
    /// <summary>The default extensions: prose formats that embed well.</summary>
    public static readonly IReadOnlyList<string> DefaultExtensions = [".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc", ".html", ".htm"];

    /// <summary>The default extensionless documentation files.</summary>
    public static readonly IReadOnlyList<string> DefaultNames = ["README", "CHANGELOG", "CONTRIBUTING"];

    /// <summary>The default per-file size cap (1 MB).</summary>
    public const long DefaultMaxFileBytes = 1024 * 1024;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist",
    };

    private static readonly EnumerationOptions TopLevelOnly = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Scans <see cref="ScanOptions.Root"/> according to <paramref name="options"/>.</summary>
    public static ScanResult Scan(ScanOptions options)
    {
        var extensions = new HashSet<string>(options.Extensions, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(options.Names, StringComparer.OrdinalIgnoreCase);
        var files = new List<ScannedFile>();
        var tooLarge = new List<string>();
        var pending = new Stack<string>();
        pending.Push(options.Root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            foreach (string sub in Directory.EnumerateDirectories(directory, "*", TopLevelOnly))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*", TopLevelOnly))
            {
                if (!extensions.Contains(Path.GetExtension(path)) && !names.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Length > options.MaxFileBytes)
                {
                    tooLarge.Add(info.FullName);
                    continue;
                }

                files.Add(new ScannedFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }

        return new ScanResult(
            files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            tooLarge.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Normalizes a user-supplied extension list (<c>"md, .TXT"</c>) to lower-case, dot-prefixed, distinct entries.
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string raw) =>
        SplitList(raw).Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Splits a comma-separated list, trimming entries and dropping empty ones.</summary>
    public static IReadOnlyList<string> SplitList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
