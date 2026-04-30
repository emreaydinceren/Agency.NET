using Agency.GraphRAG.Code.Chunker;

namespace Agency.GraphRAG.Code.ChangeDetector;

/// <summary>
/// Represents the detected repository changes that downstream indexing phases should process.
/// </summary>
public sealed record class ChangeSet
{
    /// <summary>Gets the added source files.</summary>
    public required IReadOnlyList<string> AddedFiles { get; init; }

    /// <summary>Gets the modified source files with symbol-level diffs.</summary>
    public required IReadOnlyList<ModifiedFileChange> ModifiedFiles { get; init; }

    /// <summary>Gets the deleted source files.</summary>
    public required IReadOnlyList<string> DeletedFiles { get; init; }

    /// <summary>Gets renamed files while preserving previously stored symbol identities.</summary>
    public required IReadOnlyList<RenamedFileChange> RenamedFiles { get; init; }

    /// <summary>Gets changed manifest files that should trigger manifest re-parse.</summary>
    public required IReadOnlyList<string> ManifestChanges { get; init; }
}

/// <summary>
/// Represents symbol-level changes for a modified file.
/// </summary>
/// <param name="Path">The repository-relative file path.</param>
/// <param name="Changes">The per-symbol diffs for the file.</param>
public sealed record ModifiedFileChange(string Path, IReadOnlyList<SymbolChange> Changes);

/// <summary>
/// Represents a renamed file and the symbol identities that should be preserved.
/// </summary>
/// <param name="OldPath">The previous repository-relative file path.</param>
/// <param name="NewPath">The new repository-relative file path.</param>
/// <param name="PreservedSymbolIds">The symbol identifiers associated with the previous file path.</param>
public sealed record RenamedFileChange(string OldPath, string NewPath, IReadOnlyList<Guid> PreservedSymbolIds);

/// <summary>
/// Represents the change kind for a stored symbol or current chunk.
/// </summary>
public enum SymbolChangeKind
{
    /// <summary>The symbol was newly added.</summary>
    Added,

    /// <summary>The symbol already existed but its content hash changed.</summary>
    Modified,

    /// <summary>The previously stored symbol is no longer present.</summary>
    Deleted,
}

/// <summary>
/// Represents a symbol-level diff between stored graph data and current chunks.
/// </summary>
/// <param name="Kind">The kind of change that occurred.</param>
/// <param name="SymbolId">The existing symbol identifier when available.</param>
/// <param name="ChunkId">The current chunk identifier when available.</param>
/// <param name="FullyQualifiedName">The fully-qualified symbol name used to match stored and current symbols.</param>
public sealed record SymbolChange(
    SymbolChangeKind Kind,
    Guid? SymbolId,
    string? ChunkId,
    string FullyQualifiedName);
