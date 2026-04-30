using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Agency.GraphRAG.Code.Manifest;

/// <summary>
/// Parses Python manifests using <c>pyproject.toml</c> first and <c>requirements.txt</c> as a fallback.
/// </summary>
public sealed class PythonManifestParser : IManifestParser
{
    /// <summary>
    /// Determines whether the specified manifest path is supported by the Python parser.
    /// </summary>
    /// <param name="manifestPath">The manifest path to inspect.</param>
    /// <returns><c>true</c> for <c>pyproject.toml</c> and <c>requirements.txt</c>; otherwise <c>false</c>.</returns>
    public bool CanParse(string manifestPath)
    {
        string fileName = Path.GetFileName(manifestPath);
        return string.Equals(fileName, "pyproject.toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "requirements.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a supported Python manifest.
    /// </summary>
    /// <param name="repositoryRoot">The repository root directory.</param>
    /// <param name="manifestPath">The full path to the manifest file.</param>
    /// <returns>The parsed manifest result.</returns>
    public ManifestParseResult Parse(string repositoryRoot, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string fileName = Path.GetFileName(manifestPath);
        return string.Equals(fileName, "pyproject.toml", StringComparison.OrdinalIgnoreCase)
            ? ParsePyProject(repositoryRoot, manifestPath)
            : ParseRequirements(repositoryRoot, manifestPath);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(
        ManifestParserContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManifestParseResult parsed = Parse(context.Repo.LocalPath, context.ManifestPath);
        return Task.FromResult<IReadOnlyList<ManifestProjectDefinition>>(
        [
            new ManifestProjectDefinition
            {
                Name = parsed.ProjectName,
                Language = "python",
                RelativePath = parsed.ProjectRelativePath,
                ManifestPath = parsed.ManifestRelativePath,
                ExternalPackages = parsed.ExternalDependencies
                    .Select(static dependency => new ManifestExternalPackage(dependency.Name, dependency.Version, "pypi", dependency.Scope))
                    .ToArray(),
                ReferencedProjectPaths = parsed.ProjectReferences
                    .Select(static reference => reference.ManifestRelativePath)
                    .ToArray(),
            },
        ]);
    }

    private static ManifestParseResult ParsePyProject(string repositoryRoot, string manifestPath)
    {
        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest directory could not be determined.");
        TomlTable model = Toml.ToModel(File.ReadAllText(manifestPath)) as TomlTable
            ?? throw new InvalidOperationException("pyproject.toml could not be parsed.");

        List<ManifestPackageDependency> dependencies = [];
        List<ManifestProjectReference> projectReferences = [];

        string projectName = Path.GetFileName(manifestDirectory);

        if (TryGetTable(model, "tool", out TomlTable? toolTable)
            && toolTable is not null
            && TryGetTable(toolTable, "poetry", out TomlTable? poetryTable)
            && poetryTable is not null)
        {
            if (TryGetValue(poetryTable, "name", out string? poetryName) && !string.IsNullOrWhiteSpace(poetryName))
            {
                projectName = poetryName;
            }

            if (TryGetTable(poetryTable, "dependencies", out TomlTable? poetryDependencies) && poetryDependencies is not null)
            {
                AddPoetryDependencies(poetryDependencies, "runtime");
            }

            if (TryGetTable(poetryTable, "group", out TomlTable? groupTable) && groupTable is not null)
            {
                foreach ((string groupName, object? value) in groupTable)
                {
                    if (value is TomlTable group
                        && TryGetTable(group, "dependencies", out TomlTable? groupedDependencies)
                        && groupedDependencies is not null)
                    {
                        AddPoetryDependencies(groupedDependencies, groupName);
                    }
                }
            }
        }

        Dictionary<string, string> uvPathSources = LoadUvPathSources(model, manifestDirectory, repositoryRoot);
        if (TryGetTable(model, "project", out TomlTable? projectTable) && projectTable is not null)
        {
            if (TryGetValue(projectTable, "name", out string? pep621Name) && !string.IsNullOrWhiteSpace(pep621Name))
            {
                projectName = pep621Name;
            }

            AddRequirementArray(projectTable, "dependencies", "runtime", uvPathSources);

            if (TryGetTable(projectTable, "optional-dependencies", out TomlTable? optionalDependencies) && optionalDependencies is not null)
            {
                foreach ((string groupName, object? value) in optionalDependencies)
                {
                    if (value is TomlArray)
                    {
                        AddRequirementEntries(groupName, value, uvPathSources);
                    }
                }
            }
        }

        if (TryGetTable(model, "dependency-groups", out TomlTable? dependencyGroups) && dependencyGroups is not null)
        {
            foreach ((string groupName, object? value) in dependencyGroups)
            {
                AddRequirementEntries(groupName, value, uvPathSources);
            }
        }

        return new ManifestParseResult
        {
            ProjectName = projectName,
            Ecosystem = "pypi",
            ManifestRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestPath),
            ProjectRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestDirectory),
            ExternalDependencies = dependencies,
            ProjectReferences = projectReferences,
        };

        void AddPoetryDependencies(TomlTable table, string scope)
        {
            foreach ((string dependencyName, object? dependencyValue) in table)
            {
                if (string.Equals(dependencyName, "python", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (dependencyValue)
                {
                    case string version:
                        dependencies.Add(new ManifestPackageDependency(dependencyName, version, scope));
                        break;
                    case TomlTable dependencyTable when TryGetValue(dependencyTable, "path", out string? path) && !string.IsNullOrWhiteSpace(path):
                        string referencedManifestPath = Path.GetFullPath(Path.Combine(manifestDirectory, path, "pyproject.toml"));
                        projectReferences.Add(new ManifestProjectReference(dependencyName, ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, referencedManifestPath)));
                        break;
                    case TomlTable dependencyTable when TryGetValue(dependencyTable, "version", out string? tableVersion):
                        dependencies.Add(new ManifestPackageDependency(dependencyName, tableVersion, scope));
                        break;
                }
            }
        }

        void AddRequirementArray(TomlTable table, string propertyName, string scope, IReadOnlyDictionary<string, string> pathSources)
        {
            if (!TryGetValue(table, propertyName, out TomlArray? values) || values is null)
            {
                return;
            }

            AddRequirementEntries(scope, values, pathSources);
        }

        void AddRequirementEntries(string scope, object? values, IReadOnlyDictionary<string, string> pathSources)
        {
            if (values is not TomlArray requirements)
            {
                return;
            }

            foreach (object? requirement in requirements)
            {
                if (requirement is not string requirementText)
                {
                    continue;
                }

                ParsedRequirement parsedRequirement = ParseRequirement(requirementText);
                if (pathSources.TryGetValue(parsedRequirement.Name, out string? referencedManifestPath))
                {
                    projectReferences.Add(new ManifestProjectReference(parsedRequirement.Name, referencedManifestPath));
                    continue;
                }

                dependencies.Add(new ManifestPackageDependency(parsedRequirement.Name, parsedRequirement.Version, scope));
            }
        }
    }

    private static ManifestParseResult ParseRequirements(string repositoryRoot, string manifestPath)
    {
        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest directory could not be determined.");
        string projectName = Path.GetFileName(manifestDirectory);
        List<ManifestPackageDependency> dependencies = [];

        foreach (string rawLine in File.ReadAllLines(manifestPath))
        {
            string line = rawLine.Split('#')[0].Trim();
            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith("-", StringComparison.Ordinal)
                || line.StartsWith(".", StringComparison.Ordinal)
                || line.StartsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            ParsedRequirement requirement = ParseRequirement(line);
            dependencies.Add(new ManifestPackageDependency(requirement.Name, requirement.Version, "runtime"));
        }

        return new ManifestParseResult
        {
            ProjectName = projectName,
            Ecosystem = "pypi",
            ManifestRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestPath),
            ProjectRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestDirectory),
            ExternalDependencies = dependencies,
            ProjectReferences = [],
        };
    }

    private static Dictionary<string, string> LoadUvPathSources(TomlTable model, string manifestDirectory, string repositoryRoot)
    {
        Dictionary<string, string> pathSources = new(StringComparer.OrdinalIgnoreCase);
        if (!TryGetTable(model, "tool", out TomlTable? toolTable)
            || toolTable is null
            || !TryGetTable(toolTable, "uv", out TomlTable? uvTable)
            || uvTable is null
            || !TryGetTable(uvTable, "sources", out TomlTable? sourcesTable)
            || sourcesTable is null)
        {
            return pathSources;
        }

        foreach ((string name, object? source) in sourcesTable)
        {
            if (source is TomlTable sourceTable
                && TryGetValue(sourceTable, "path", out string? path)
                && !string.IsNullOrWhiteSpace(path))
            {
                string referencedManifestPath = Path.GetFullPath(Path.Combine(manifestDirectory, path, "pyproject.toml"));
                pathSources[name] = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, referencedManifestPath);
            }
        }

        return pathSources;
    }

    private static ParsedRequirement ParseRequirement(string requirement)
    {
        string normalizedRequirement = requirement.Split(';')[0].Trim();
        Match match = Regex.Match(normalizedRequirement, @"^(?<name>[A-Za-z0-9_.\-]+)(?:\[[^\]]+\])?(?<version>.*)$");
        if (!match.Success)
        {
            return new ParsedRequirement(normalizedRequirement, null);
        }

        string name = match.Groups["name"].Value;
        string version = match.Groups["version"].Value.Trim();
        return new ParsedRequirement(name, string.IsNullOrWhiteSpace(version) ? null : version);
    }

    private static bool TryGetTable(TomlTable table, string key, out TomlTable? result)
    {
        if (table.TryGetValue(key, out object? value) && value is TomlTable nestedTable)
        {
            result = nestedTable;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryGetValue<T>(TomlTable table, string key, out T? result)
    {
        if (table.TryGetValue(key, out object? value) && value is T typedValue)
        {
            result = typedValue;
            return true;
        }

        result = default;
        return false;
    }

    private sealed record ParsedRequirement(string Name, string? Version);
}
