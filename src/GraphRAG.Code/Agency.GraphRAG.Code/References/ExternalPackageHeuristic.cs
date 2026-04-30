using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.References;

/// <summary>
/// Detects whether an identifier likely refers to a symbol provided by a declared external package.
/// </summary>
public sealed class ExternalPackageHeuristic
{
    /// <summary>
    /// Finds the best matching external package for an identifier or fully-qualified target.
    /// </summary>
    /// <param name="identifier">The unresolved identifier or extracted target.</param>
    /// <param name="packages">The declared external packages in scope.</param>
    /// <returns>The matched package name, or <c>null</c> when no package prefix matches.</returns>
    public string? MatchPackage(string identifier, IReadOnlyList<ExternalPackage> packages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(packages);

        string normalizedIdentifier = Normalize(identifier);

        return packages
            .Select(static package => package.Name)
            .Where(static packageName => !string.IsNullOrWhiteSpace(packageName))
            .Select(packageName => new { PackageName = packageName, Normalized = Normalize(packageName) })
            .Where(candidate =>
                normalizedIdentifier.Equals(candidate.Normalized, StringComparison.OrdinalIgnoreCase)
                || normalizedIdentifier.StartsWith(candidate.Normalized + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static candidate => candidate.Normalized.Length)
            .Select(static candidate => candidate.PackageName)
            .FirstOrDefault();
    }

    private static string Normalize(string value) => value.Replace('/', '.').Replace('\\', '.').Trim();
}
