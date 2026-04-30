using System.Xml.Linq;

namespace Agency.GraphRAG.Code.Manifest;

/// <summary>
/// Parses C# project manifests and central package version files.
/// </summary>
public sealed class CSharpManifestParser : IManifestParser
{
    /// <summary>
    /// Determines whether the specified manifest path is a supported C# manifest.
    /// </summary>
    /// <param name="manifestPath">The manifest path to inspect.</param>
    /// <returns><c>true</c> when the file is a <c>.csproj</c>; otherwise <c>false</c>.</returns>
    public bool CanParse(string manifestPath)
    {
        return string.Equals(Path.GetExtension(manifestPath), ".csproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a C# project manifest.
    /// </summary>
    /// <param name="repositoryRoot">The repository root directory.</param>
    /// <param name="manifestPath">The full path to the project file.</param>
    /// <returns>The parsed manifest result.</returns>
    public ManifestParseResult Parse(string repositoryRoot, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        XDocument projectDocument = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace);
        string manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("Manifest directory could not be determined.");
        Dictionary<string, string> centralVersions = LoadCentralPackageVersions(repositoryRoot, manifestDirectory);

        List<ManifestPackageDependency> packages = [];
        foreach (XElement packageReference in projectDocument.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
        {
            string? packageName = packageReference.Attribute("Include")?.Value ?? packageReference.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            string? version =
                packageReference.Attribute("Version")?.Value
                ?? packageReference.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;

            if (string.IsNullOrWhiteSpace(version) && centralVersions.TryGetValue(packageName, out string? centralVersion))
            {
                version = centralVersion;
            }

            packages.Add(new ManifestPackageDependency(packageName, string.IsNullOrWhiteSpace(version) ? null : version, "runtime"));
        }

        List<ManifestProjectReference> projectReferences = [];
        foreach (XElement projectReference in projectDocument.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
        {
            string? includePath = projectReference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(includePath))
            {
                continue;
            }

            string referencedManifestPath = Path.GetFullPath(Path.Combine(manifestDirectory, includePath));
            string? referenceName =
                projectReference.Elements().FirstOrDefault(element => element.Name.LocalName == "Name")?.Value
                ?? Path.GetFileNameWithoutExtension(referencedManifestPath);

            projectReferences.Add(new ManifestProjectReference(referenceName, ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, referencedManifestPath)));
        }

        string projectName =
            projectDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value
            ?? projectDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == "PackageId")?.Value
            ?? Path.GetFileNameWithoutExtension(manifestPath);

        return new ManifestParseResult
        {
            ProjectName = projectName,
            Ecosystem = "nuget",
            ManifestRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestPath),
            ProjectRelativePath = ManifestPathUtilities.NormalizeRelativePath(repositoryRoot, manifestDirectory),
            ExternalDependencies = packages,
            ProjectReferences = projectReferences,
        };
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
                Language = "csharp",
                RelativePath = parsed.ProjectRelativePath,
                ManifestPath = parsed.ManifestRelativePath,
                ExternalPackages = parsed.ExternalDependencies
                    .Select(static dependency => new ManifestExternalPackage(dependency.Name, dependency.Version, "nuget", dependency.Scope))
                    .ToArray(),
                ReferencedProjectPaths = parsed.ProjectReferences
                    .Select(static reference => reference.ManifestRelativePath)
                    .ToArray(),
            },
        ]);
    }

    private static Dictionary<string, string> LoadCentralPackageVersions(string repositoryRoot, string manifestDirectory)
    {
        List<string> propsFiles = [];
        string? currentDirectory = manifestDirectory;
        string repositoryRootFullPath = Path.GetFullPath(repositoryRoot);

        while (currentDirectory is not null)
        {
            string candidate = Path.Combine(currentDirectory, "Directory.Packages.props");
            if (File.Exists(candidate))
            {
                propsFiles.Add(candidate);
            }

            if (string.Equals(Path.GetFullPath(currentDirectory), repositoryRootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        }

        propsFiles.Reverse();

        Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
        foreach (string propsFile in propsFiles)
        {
            XDocument propsDocument = XDocument.Load(propsFile, LoadOptions.PreserveWhitespace);
            foreach (XElement packageVersion in propsDocument.Descendants().Where(element => element.Name.LocalName == "PackageVersion"))
            {
                string? packageName = packageVersion.Attribute("Include")?.Value ?? packageVersion.Attribute("Update")?.Value;
                string? version =
                    packageVersion.Attribute("Version")?.Value
                    ?? packageVersion.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;

                if (!string.IsNullOrWhiteSpace(packageName) && !string.IsNullOrWhiteSpace(version))
                {
                    versions[packageName] = version;
                }
            }
        }

        return versions;
    }
}
