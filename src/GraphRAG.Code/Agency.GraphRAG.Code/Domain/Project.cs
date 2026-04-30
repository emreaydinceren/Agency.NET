namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a single build project (e.g. a .csproj, package.json) within a repository.
/// </summary>
public record class Project
{
    /// <summary>Gets the unique identifier for this project.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the repository that contains this project.</summary>
    public required Guid RepoId { get; init; }

    /// <summary>Gets the primary programming language of this project (e.g. "csharp", "typescript").</summary>
    public required string Language { get; init; }

    /// <summary>Gets the human-readable name of the project.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the path to this project relative to the repository root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the path to the project manifest file (e.g. .csproj, package.json), or <c>null</c> if absent.</summary>
    public string? ManifestPath { get; init; }
}
