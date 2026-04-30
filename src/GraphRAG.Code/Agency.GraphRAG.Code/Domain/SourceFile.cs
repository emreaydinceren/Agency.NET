namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a single source file within a project.
/// Named <c>SourceFile</c> to avoid collision with <see cref="System.IO.File"/>.
/// </summary>
public record class SourceFile
{
    /// <summary>Gets the unique identifier for this file.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the project that owns this file.</summary>
    public required Guid ProjectId { get; init; }

    /// <summary>Gets the identifier of the repository that contains this file.</summary>
    public required Guid RepoId { get; init; }

    /// <summary>Gets the path to this file, relative to the repository root.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the programming language of this file (e.g. "csharp", "typescript").</summary>
    public required string Language { get; init; }

    /// <summary>Gets the hash of the file content, or <c>null</c> if not computed.</summary>
    public string? ContentHash { get; init; }
}
