using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.ChangeDetector;

/// <summary>
/// Compares a repository walk result against stored symbol hashes to determine downstream indexing work.
/// </summary>
public sealed class ChangeDetector
{
    private static readonly HashSet<string> ManifestFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json",
        "package-lock.json",
        "pnpm-lock.yaml",
        "yarn.lock",
        "pyproject.toml",
        "requirements.txt",
        "Directory.Packages.props",
    };

    /// <summary>
    /// Detects file- and symbol-level changes using stored symbols and current file chunks.
    /// </summary>
    /// <param name="walkResult">The repository walk result.</param>
    /// <param name="storedSymbolsByPath">Stored symbols grouped by repository-relative file path.</param>
    /// <param name="currentChunksByPath">Current chunks grouped by repository-relative file path.</param>
    /// <returns>The detected change set.</returns>
    public ChangeSet Detect(
        WalkResult walkResult,
        IReadOnlyDictionary<string, IReadOnlyList<Symbol>> storedSymbolsByPath,
        IReadOnlyDictionary<string, IReadOnlyList<Chunk>> currentChunksByPath)
    {
        ArgumentNullException.ThrowIfNull(walkResult);
        ArgumentNullException.ThrowIfNull(storedSymbolsByPath);
        ArgumentNullException.ThrowIfNull(currentChunksByPath);

        List<string> addedFiles = [];
        List<ModifiedFileChange> modifiedFiles = [];
        List<string> deletedFiles = [];
        List<RenamedFileChange> renamedFiles = [];
        HashSet<string> manifestChanges = new(StringComparer.OrdinalIgnoreCase);

        foreach (WalkedFile file in walkResult.Files)
        {
            bool isManifestChange = TrackManifestChange(file, manifestChanges);

            switch (file.Status)
            {
                case WalkedFileStatus.Added:
                    addedFiles.Add(file.Path);
                    break;

                case WalkedFileStatus.Modified:
                    if (!isManifestChange)
                    {
                        modifiedFiles.Add(new ModifiedFileChange(file.Path, DetectSymbolChanges(file.Path, storedSymbolsByPath, currentChunksByPath)));
                    }
                    break;

                case WalkedFileStatus.Deleted:
                    deletedFiles.Add(file.Path);
                    break;

                case WalkedFileStatus.Renamed:
                    renamedFiles.Add(CreateRename(file, storedSymbolsByPath));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(file.Status), file.Status, "Unsupported walked file status.");
            }
        }

        return new ChangeSet
        {
            AddedFiles = addedFiles,
            ModifiedFiles = modifiedFiles,
            DeletedFiles = deletedFiles,
            RenamedFiles = renamedFiles,
            ManifestChanges = manifestChanges.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static IReadOnlyList<SymbolChange> DetectSymbolChanges(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<Symbol>> storedSymbolsByPath,
        IReadOnlyDictionary<string, IReadOnlyList<Chunk>> currentChunksByPath)
    {
        IReadOnlyList<Symbol> storedSymbols = storedSymbolsByPath.TryGetValue(path, out IReadOnlyList<Symbol>? existingSymbols)
            ? existingSymbols
            : [];
        IReadOnlyList<Chunk> currentChunks = currentChunksByPath.TryGetValue(path, out IReadOnlyList<Chunk>? chunks)
            ? chunks
            : [];

        Dictionary<string, Symbol> storedByName = storedSymbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol.FullyQualifiedName))
            .GroupBy(static symbol => symbol.FullyQualifiedName!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        Dictionary<string, Chunk> currentByName = currentChunks
            .Where(static chunk => !string.IsNullOrWhiteSpace(chunk.FullyQualifiedName))
            .GroupBy(static chunk => chunk.FullyQualifiedName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        HashSet<string> allNames = [.. storedByName.Keys, .. currentByName.Keys];
        List<SymbolChange> changes = [];

        foreach (string name in allNames.OrderBy(static value => value, StringComparer.Ordinal))
        {
            bool hasStored = storedByName.TryGetValue(name, out Symbol? stored);
            bool hasCurrent = currentByName.TryGetValue(name, out Chunk? current);

            if (hasStored && hasCurrent)
            {
                ArgumentNullException.ThrowIfNull(current);
                string currentHash = ComputeContentHash(current.Content);
                if (!string.Equals(stored!.ContentHash, currentHash, StringComparison.Ordinal))
                {
                    changes.Add(new SymbolChange(SymbolChangeKind.Modified, stored.Id, current.Id, name));
                }

                continue;
            }

            if (hasCurrent)
            {
                changes.Add(new SymbolChange(SymbolChangeKind.Added, null, current!.Id, name));
                continue;
            }

            changes.Add(new SymbolChange(SymbolChangeKind.Deleted, stored!.Id, null, name));
        }

        return changes;
    }

    private static RenamedFileChange CreateRename(
        WalkedFile file,
        IReadOnlyDictionary<string, IReadOnlyList<Symbol>> storedSymbolsByPath)
    {
        if (string.IsNullOrWhiteSpace(file.OldPath))
        {
            throw new InvalidOperationException("Renamed files must include an old path.");
        }

        IReadOnlyList<Guid> preservedSymbolIds = storedSymbolsByPath.TryGetValue(file.OldPath, out IReadOnlyList<Symbol>? symbols)
            ? symbols.Select(static symbol => symbol.Id).ToArray()
            : [];

        return new RenamedFileChange(file.OldPath, file.Path, preservedSymbolIds);
    }

    private static bool TrackManifestChange(WalkedFile file, ISet<string> manifestChanges)
    {
        bool tracked = false;

        if (IsManifestPath(file.Path))
        {
            manifestChanges.Add(file.Path);
            tracked = true;
        }

        if (!string.IsNullOrWhiteSpace(file.OldPath) && IsManifestPath(file.OldPath))
        {
            manifestChanges.Add(file.OldPath);
            tracked = true;
        }

        return tracked;
    }

    private static bool IsManifestPath(string path)
    {
        string fileName = Path.GetFileName(path);
        if (ManifestFileNames.Contains(fileName))
        {
            return true;
        }

        return fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeContentHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
