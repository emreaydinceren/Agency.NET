using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agency.GraphRAG.Code.Manifest;

/// <summary>
/// Parses npm-compatible manifests, lockfiles, and workspace layouts.
/// </summary>
public sealed class NpmManifestParser : IManifestParser
{
    /// <summary>
    /// Determines whether the specified manifest path is a supported npm manifest.
    /// </summary>
    /// <param name="manifestPath">The manifest path to inspect.</param>
    /// <returns><c>true</c> when the file is a <c>package.json</c>; otherwise <c>false</c>.</returns>
    public bool CanParse(string manifestPath)
    {
        return string.Equals(Path.GetFileName(manifestPath), "package.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a package manifest.
    /// </summary>
    /// <param name="repositoryRoot">The repository root directory.</param>
    /// <param name="manifestPath">The full path to the package.json file.</param>
    /// <returns>The parsed manifest result.</returns>
    public ManifestParseResult Parse(string repositoryRoot, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        using JsonDocument packageDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement packageRoot = packageDocument.RootElement;
        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest directory could not be determined.");
        string projectName = packageRoot.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? Path.GetFileName(manifestDirectory) : Path.GetFileName(manifestDirectory);
        WorkspaceContext workspaceContext = WorkspaceContext.Create(repositoryRoot, manifestPath);
        IReadOnlyDictionary<string, string> resolvedVersions = LoadResolvedVersions(repositoryRoot, manifestPath, workspaceContext);

        List<ManifestPackageDependency> externalDependencies = [];
        List<ManifestProjectReference> projectReferences = [];
        HashSet<string> referencedProjects = new(StringComparer.OrdinalIgnoreCase);

        AddDependencies("dependencies", "runtime");
        AddDependencies("devDependencies", "dev");
        AddDependencies("peerDependencies", "peer");
        AddDependencies("optionalDependencies", "optional");

        return new ManifestParseResult
        {
            ProjectName = projectName,
            Ecosystem = "npm",
            ManifestRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestPath),
            ProjectRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestDirectory),
            ExternalDependencies = externalDependencies,
            ProjectReferences = projectReferences,
        };

        void AddDependencies(string propertyName, string scope)
        {
            if (!packageRoot.TryGetProperty(propertyName, out JsonElement dependenciesElement) || dependenciesElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty dependency in dependenciesElement.EnumerateObject())
            {
                string packageName = dependency.Name;
                string? declaredVersion = dependency.Value.GetString();

                if (workspaceContext.TryResolveProject(packageName, out string? referencedManifestRelativePath)
                    && !string.Equals(referencedManifestRelativePath, ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestPath), StringComparison.OrdinalIgnoreCase))
                {
                    if (referencedProjects.Add(referencedManifestRelativePath))
                    {
                        projectReferences.Add(new ManifestProjectReference(packageName, referencedManifestRelativePath));
                    }

                    continue;
                }

                string? version = resolvedVersions.TryGetValue(packageName, out string? resolvedVersion) ? resolvedVersion : declaredVersion;
                externalDependencies.Add(new ManifestPackageDependency(packageName, version, scope));
            }
        }
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
                Language = "typescript",
                RelativePath = parsed.ProjectRelativePath,
                ManifestPath = parsed.ManifestRelativePath,
                ExternalPackages = parsed.ExternalDependencies
                    .Select(static dependency => new ManifestExternalPackage(dependency.Name, dependency.Version, "npm", dependency.Scope))
                    .ToArray(),
                ReferencedProjectPaths = parsed.ProjectReferences
                    .Select(static reference => reference.ManifestRelativePath)
                    .ToArray(),
            },
        ]);
    }

    private static IReadOnlyDictionary<string, string> LoadResolvedVersions(string repositoryRoot, string manifestPath, WorkspaceContext workspaceContext)
    {
        string? pnpmLockFile = FindNearestFile(repositoryRoot, manifestPath, "pnpm-lock.yaml");
        if (pnpmLockFile is not null)
        {
            return ParsePnpmLockFile(pnpmLockFile, workspaceContext);
        }

        string? packageLockFile = FindNearestFile(repositoryRoot, manifestPath, "package-lock.json");
        if (packageLockFile is not null)
        {
            return ParsePackageLockFile(packageLockFile);
        }

        string? yarnLockFile = FindNearestFile(repositoryRoot, manifestPath, "yarn.lock");
        return yarnLockFile is not null ? ParseYarnLockFile(yarnLockFile) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindNearestFile(string repositoryRoot, string manifestPath, string fileName)
    {
        string? currentDirectory = Path.GetDirectoryName(manifestPath);
        string repositoryRootFullPath = Path.GetFullPath(repositoryRoot);

        while (currentDirectory is not null)
        {
            string candidate = Path.Combine(currentDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (string.Equals(Path.GetFullPath(currentDirectory), repositoryRootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> ParsePackageLockFile(string lockFilePath)
    {
        Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockFilePath));
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("packages", out JsonElement packagesElement) && packagesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty package in packagesElement.EnumerateObject())
            {
                if (!package.Name.StartsWith("node_modules/", StringComparison.Ordinal))
                {
                    continue;
                }

                string packageName = package.Name["node_modules/".Length..];
                if (package.Value.TryGetProperty("version", out JsonElement versionElement))
                {
                    string? version = versionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        versions[packageName] = version;
                    }
                }
            }
        }

        if (versions.Count == 0 && root.TryGetProperty("dependencies", out JsonElement dependenciesElement) && dependenciesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty dependency in dependenciesElement.EnumerateObject())
            {
                if (dependency.Value.TryGetProperty("version", out JsonElement versionElement))
                {
                    string? version = versionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        versions[dependency.Name] = version;
                    }
                }
            }
        }

        return versions;
    }

    private static IReadOnlyDictionary<string, string> ParsePnpmLockFile(string lockFilePath, WorkspaceContext workspaceContext)
    {
        Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
        string importerKey = workspaceContext.ImporterKey;
        string? currentImporter = null;
        bool inDependenciesBlock = false;
        string? currentDependency = null;

        foreach (string rawLine in File.ReadAllLines(lockFilePath))
        {
            string line = rawLine.Replace('\t', ' ');
            int indent = line.TakeWhile(character => character == ' ').Count();
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (indent == 0 && trimmed == "importers:")
            {
                continue;
            }

            if (indent == 2 && trimmed.EndsWith(':'))
            {
                currentImporter = trimmed.TrimEnd(':').Trim('\'', '"');
                inDependenciesBlock = false;
                currentDependency = null;
                continue;
            }

            if (!string.Equals(currentImporter, importerKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (indent == 4 && trimmed is "dependencies:" or "devDependencies:" or "optionalDependencies:" or "peerDependencies:")
            {
                inDependenciesBlock = true;
                currentDependency = null;
                continue;
            }

            if (indent == 4 && trimmed.EndsWith(':'))
            {
                inDependenciesBlock = false;
                currentDependency = null;
                continue;
            }

            if (!inDependenciesBlock)
            {
                continue;
            }

            if (indent == 6 && trimmed.EndsWith(':'))
            {
                currentDependency = trimmed.TrimEnd(':').Trim('\'', '"');
                continue;
            }

            if (indent >= 8 && currentDependency is not null && trimmed.StartsWith("version:", StringComparison.Ordinal))
            {
                string version = trimmed["version:".Length..].Trim().Trim('\'', '"');
                if (!string.IsNullOrWhiteSpace(version) && !version.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
                {
                    versions[currentDependency] = version;
                }
            }
        }

        return versions;
    }

    private static IReadOnlyDictionary<string, string> ParseYarnLockFile(string lockFilePath)
    {
        Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
        string? currentPackage = null;

        foreach (string rawLine in File.ReadAllLines(lockFilePath))
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(rawLine[0]) && trimmed.EndsWith(':'))
            {
                currentPackage = ParseYarnPackageName(trimmed.TrimEnd(':'));
                continue;
            }

            if (currentPackage is not null && trimmed.StartsWith("version ", StringComparison.Ordinal))
            {
                string version = trimmed["version ".Length..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(version))
                {
                    versions[currentPackage] = version;
                }
            }
        }

        return versions;
    }

    private static string ParseYarnPackageName(string selectorList)
    {
        string firstSelector = selectorList.Split(',')[0].Trim().Trim('"');
        if (firstSelector.StartsWith('@'))
        {
            int separatorIndex = firstSelector.IndexOf('@', 1);
            return separatorIndex > 0 ? firstSelector[..separatorIndex] : firstSelector;
        }

        int atIndex = firstSelector.IndexOf('@');
        return atIndex > 0 ? firstSelector[..atIndex] : firstSelector;
    }

    private sealed class WorkspaceContext
    {
        private WorkspaceContext(string importerKey, IReadOnlyDictionary<string, string> packagesByName)
        {
            ImporterKey = importerKey;
            PackagesByName = packagesByName;
        }

        public string ImporterKey { get; }

        public IReadOnlyDictionary<string, string> PackagesByName { get; }

        public bool TryResolveProject(string packageName, out string manifestRelativePath)
        {
            return PackagesByName.TryGetValue(packageName, out manifestRelativePath!);
        }

        public static WorkspaceContext Create(string repositoryRoot, string manifestPath)
        {
            string manifestDirectory = Path.GetDirectoryName(manifestPath)
                ?? throw new InvalidOperationException("Manifest directory could not be determined.");
            string importerKey = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestDirectory);
            importerKey = string.IsNullOrEmpty(importerKey) ? "." : importerKey;
            Dictionary<string, string> packagesByName = new(StringComparer.OrdinalIgnoreCase);

            foreach (string workspaceManifest in DiscoverWorkspaceManifests(repositoryRoot, manifestPath))
            {
                using JsonDocument packageDocument = JsonDocument.Parse(File.ReadAllText(workspaceManifest));
                if (!packageDocument.RootElement.TryGetProperty("name", out JsonElement nameElement))
                {
                    continue;
                }

                string? packageName = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    continue;
                }

                packagesByName[packageName] = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, workspaceManifest);
            }

            return new WorkspaceContext(importerKey, packagesByName);
        }

        private static IEnumerable<string> DiscoverWorkspaceManifests(string repositoryRoot, string manifestPath)
        {
            string? currentDirectory = Path.GetDirectoryName(manifestPath);
            string repositoryRootFullPath = Path.GetFullPath(repositoryRoot);
            HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

            while (currentDirectory is not null)
            {
                string packageJsonPath = Path.Combine(currentDirectory, "package.json");
                if (File.Exists(packageJsonPath))
                {
                    foreach (string workspaceManifest in ExpandWorkspacePackageJson(repositoryRoot, currentDirectory, packageJsonPath))
                    {
                        results.Add(workspaceManifest);
                    }
                }

                string pnpmWorkspacePath = Path.Combine(currentDirectory, "pnpm-workspace.yaml");
                if (File.Exists(pnpmWorkspacePath))
                {
                    foreach (string workspaceManifest in ExpandPnpmWorkspace(repositoryRoot, currentDirectory, pnpmWorkspacePath))
                    {
                        results.Add(workspaceManifest);
                    }
                }

                if (string.Equals(Path.GetFullPath(currentDirectory), repositoryRootFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
            }

            return results;
        }

        private static IEnumerable<string> ExpandWorkspacePackageJson(string repositoryRoot, string workspaceRoot, string packageJsonPath)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!TryGetWorkspacePatterns(document.RootElement, out IReadOnlyList<string>? patterns))
            {
                return [];
            }

            return ExpandPatterns(repositoryRoot, workspaceRoot, patterns!);
        }

        private static bool TryGetWorkspacePatterns(JsonElement root, out IReadOnlyList<string>? patterns)
        {
            patterns = null;
            if (!root.TryGetProperty("workspaces", out JsonElement workspacesElement))
            {
                return false;
            }

            if (workspacesElement.ValueKind == JsonValueKind.Array)
            {
                patterns = workspacesElement.EnumerateArray().Select(element => element.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
                return patterns.Count > 0;
            }

            if (workspacesElement.ValueKind == JsonValueKind.Object
                && workspacesElement.TryGetProperty("packages", out JsonElement packagesElement)
                && packagesElement.ValueKind == JsonValueKind.Array)
            {
                patterns = packagesElement.EnumerateArray().Select(element => element.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
                return patterns.Count > 0;
            }

            return false;
        }

        private static IEnumerable<string> ExpandPnpmWorkspace(string repositoryRoot, string workspaceRoot, string pnpmWorkspacePath)
        {
            List<string> patterns = [];
            bool inPackages = false;
            foreach (string rawLine in File.ReadAllLines(pnpmWorkspacePath))
            {
                string trimmed = rawLine.Trim();
                if (trimmed == "packages:")
                {
                    inPackages = true;
                    continue;
                }

                if (!inPackages || !trimmed.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                patterns.Add(trimmed[1..].Trim().Trim('\'', '"'));
            }

            return ExpandPatterns(repositoryRoot, workspaceRoot, patterns);
        }

        private static IEnumerable<string> ExpandPatterns(string repositoryRoot, string workspaceRoot, IEnumerable<string> patterns)
        {
            HashSet<string> manifests = new(StringComparer.OrdinalIgnoreCase);
            foreach (string pattern in patterns)
            {
                string regexPattern = "^" + Regex.Escape(pattern.Replace('\\', '/'))
                    .Replace(@"\*\*", ".*")
                    .Replace(@"\*", @"[^/]*")
                    .Replace(@"\?", ".") + "$";
                Regex regex = new(regexPattern, RegexOptions.IgnoreCase);

                foreach (string packageJsonPath in Directory.EnumerateFiles(workspaceRoot, "package.json", SearchOption.AllDirectories))
                {
                    string relativeDirectory = ManifestPathUtilities.NormalizeRelativePath(workspaceRoot, Path.GetDirectoryName(packageJsonPath)!);
                    if (string.IsNullOrEmpty(relativeDirectory))
                    {
                        continue;
                    }

                    if (regex.IsMatch(relativeDirectory))
                    {
                        manifests.Add(Path.GetFullPath(packageJsonPath));
                    }
                }
            }

            return manifests.Where(path => path.StartsWith(Path.GetFullPath(repositoryRoot), StringComparison.OrdinalIgnoreCase));
        }
    }
}
