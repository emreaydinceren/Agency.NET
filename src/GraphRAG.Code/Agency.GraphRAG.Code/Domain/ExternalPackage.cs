namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents an external package dependency declared by a project.
/// </summary>
public record class ExternalPackage
{
    /// <summary>Gets the unique identifier for this dependency record.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the identifier of the project that declares this dependency.</summary>
    public required Guid ProjectId { get; init; }

    /// <summary>Gets the package name (e.g. "Newtonsoft.Json", "react").</summary>
    public required string Name { get; init; }

    /// <summary>Gets the declared version string, or <c>null</c> if unversioned.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the package ecosystem (e.g. "nuget", "npm", "pypi").</summary>
    public required string Ecosystem { get; init; }

    /// <summary>Gets the dependency scope (e.g. "runtime", "dev", "peer").</summary>
    public required string Scope { get; init; }
}
