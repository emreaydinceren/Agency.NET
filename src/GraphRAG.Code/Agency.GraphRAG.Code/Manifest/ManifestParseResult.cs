namespace Agency.GraphRAG.Code.Manifest;

/// <summary>
/// Represents the dependency information extracted from a manifest file.
/// </summary>
public sealed record class ManifestParseResult
{
    /// <summary>Gets the parsed project name.</summary>
    public required string ProjectName { get; init; }

    /// <summary>Gets the manifest path relative to the repository root.</summary>
    public required string ManifestRelativePath { get; init; }

    /// <summary>Gets the project directory path relative to the repository root.</summary>
    public required string ProjectRelativePath { get; init; }

    /// <summary>Gets the package ecosystem (for example <c>nuget</c>, <c>npm</c>, or <c>pypi</c>).</summary>
    public required string Ecosystem { get; init; }

    /// <summary>Gets the external package dependencies declared by the manifest.</summary>
    public required IReadOnlyList<ManifestPackageDependency> ExternalDependencies { get; init; }

    /// <summary>Gets the intra-repository project references discovered from the manifest.</summary>
    public required IReadOnlyList<ManifestProjectReference> ProjectReferences { get; init; }
}

/// <summary>
/// Represents a direct external package dependency declared by a project manifest.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Version">The requested or resolved version string.</param>
/// <param name="Scope">The dependency scope.</param>
public sealed record class ManifestPackageDependency(string Name, string? Version, string Scope);

/// <summary>
/// Represents an intra-repository reference from one project manifest to another.
/// </summary>
/// <param name="Name">The referenced project name, when available.</param>
/// <param name="ManifestRelativePath">The referenced manifest path relative to the repository root.</param>
public sealed record class ManifestProjectReference(string? Name, string ManifestRelativePath);
