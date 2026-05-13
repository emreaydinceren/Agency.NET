using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using System.Collections.Concurrent;

namespace Agency.GraphRAG.Code.Cli;

/// <summary>
/// Loads raw source text for a symbol by reading the corresponding source file from the local file system.
/// File contents are cached in memory (keyed by absolute path) for the lifetime of the process,
/// since query execution is short-lived and many symbols typically share the same file.
/// </summary>
/// <remarks>
/// Root-path resolution looks up <c>root_path</c> from the indexed repo row via <see cref="IGraphStore.GetRepoLocalPathAsync"/>.
/// </remarks>
internal sealed class FileSystemSymbolTextProvider(
    IGraphStore graphStore) : ISymbolTextProvider
{
    private readonly ConcurrentDictionary<string, string[]> _fileLineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string?> _repoRootCache = new();

    /// <inheritdoc />
    public async Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        SourceFile? sourceFile = await graphStore.GetFileByIdAsync(symbol.FileId, cancellationToken).ConfigureAwait(false);
        if (sourceFile is null)
        {
            return null;
        }

        string? repoRoot = await ResolveRepoRootAsync(sourceFile, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        string absolutePath = Path.Combine(repoRoot, sourceFile.Path);
        string[] lines = await LoadLinesAsync(absolutePath, cancellationToken).ConfigureAwait(false);

        int startLine = symbol.SourceRangeStart;
        int endLine = symbol.SourceRangeEnd;

        if (startLine < 0 || startLine >= lines.Length || endLine < startLine)
        {
            return null;
        }

        int clampedEnd = Math.Min(endLine, lines.Length - 1);
        return string.Join(Environment.NewLine, lines[startLine..(clampedEnd + 1)]);
    }

    private async Task<string?> ResolveRepoRootAsync(SourceFile sourceFile, CancellationToken cancellationToken)
    {
        if (this._repoRootCache.TryGetValue(sourceFile.RepoId, out string? cached))
        {
            return cached;
        }

        string? root = await graphStore.GetRepoLocalPathAsync(sourceFile.RepoId, cancellationToken).ConfigureAwait(false);
        this._repoRootCache.TryAdd(sourceFile.RepoId, root);
        return root;
    }

    private async Task<string[]> LoadLinesAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (this._fileLineCache.TryGetValue(absolutePath, out string[]? cached))
        {
            return cached;
        }

        if (!File.Exists(absolutePath))
        {
            return [];
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        this._fileLineCache.TryAdd(absolutePath, lines);
        return lines;
    }
}
