using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Builds chunks with stable identifiers.
/// </summary>
public static class ChunkBuilder
{
    /// <summary>
    /// Creates a stable chunk identifier from the path, symbol name, and signature.
    /// </summary>
    /// <param name="path">The repository-relative file path.</param>
    /// <param name="fullyQualifiedName">The fully-qualified symbol name.</param>
    /// <param name="signature">The signature.</param>
    /// <returns>The stable hash.</returns>
    public static string CreateStableId(string path, string fullyQualifiedName, string? signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedName);

        string normalizedPath = path.Replace('/', '\\');
        byte[] payload = Encoding.UTF8.GetBytes($"{normalizedPath}\n{fullyQualifiedName}\n{signature ?? string.Empty}");
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Creates a statement child symbol name.
    /// </summary>
    /// <param name="parentFullyQualifiedName">The parent symbol name.</param>
    /// <param name="index">The one-based statement index.</param>
    /// <returns>The statement symbol name.</returns>
    public static string CreateStatementSymbolName(string parentFullyQualifiedName, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFullyQualifiedName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        return $"{parentFullyQualifiedName}#statement:{index}";
    }

    /// <summary>
    /// Creates a chunk.
    /// </summary>
    /// <param name="path">The repository-relative file path.</param>
    /// <param name="language">The source language.</param>
    /// <param name="granularity">The chunk granularity.</param>
    /// <param name="name">The simple symbol or statement name.</param>
    /// <param name="fullyQualifiedName">The fully-qualified symbol name.</param>
    /// <param name="signature">The signature.</param>
    /// <param name="content">The raw source text.</param>
    /// <param name="range">The source range.</param>
    /// <param name="symbolKind">The symbol kind.</param>
    /// <param name="importsInScope">Visible imports.</param>
    /// <param name="parentId">The parent chunk identifier.</param>
    /// <param name="inherits">Base types.</param>
    /// <param name="implements">Implemented interfaces.</param>
    /// <returns>The built chunk.</returns>
    public static Chunk Build(
        string path,
        Language language,
        ChunkGranularity granularity,
        string name,
        string fullyQualifiedName,
        string? signature,
        string content,
        ChunkSourceRange range,
        SymbolKind symbolKind,
        IReadOnlyList<ImportReference> importsInScope,
        string? parentId = null,
        IReadOnlyList<string>? inherits = null,
        IReadOnlyList<string>? implements = null)
    {
        string normalizedPath = path.Replace('/', '\\');

        return new Chunk(
            CreateStableId(normalizedPath, fullyQualifiedName, signature),
            normalizedPath,
            language,
            granularity,
            name,
            fullyQualifiedName,
            signature,
            content,
            range,
            symbolKind,
            importsInScope,
            parentId,
            inherits ?? [],
            implements ?? []);
    }
}
