using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.Logging;

namespace Agency.GraphRAG.Code.Manifest;

/// <summary>
/// Defines a parser capable of extracting project dependency data from a manifest file.
/// </summary>
public interface IManifestParser
{
    /// <summary>
    /// Determines whether this parser can handle the specified repository-relative manifest path.
    /// </summary>
    /// <param name="manifestRelativePath">The manifest path relative to the repository root.</param>
    /// <returns><c>true</c> when the parser can process the manifest; otherwise <c>false</c>.</returns>
    bool CanParse(string manifestRelativePath);

    /// <summary>
    /// Parses a manifest file into one or more project definitions.
    /// </summary>
    /// <param name="context">The manifest parse context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed project definitions.</returns>
    Task<IReadOnlyList<ManifestProjectDefinition>> ParseAsync(
        ManifestParserContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides the context required to parse a manifest file.
/// </summary>
/// <param name="Repo">The repository that owns the manifest.</param>
/// <param name="ManifestPath">The absolute manifest path.</param>
/// <param name="ManifestRelativePath">The manifest path relative to the repository root.</param>
public sealed record ManifestParserContext(
    Repo Repo,
    string ManifestPath,
    string ManifestRelativePath);

/// <summary>
/// Represents an external package dependency declared by a parsed manifest.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Version">The declared version, if any.</param>
/// <param name="Ecosystem">The package ecosystem.</param>
/// <param name="Scope">The dependency scope.</param>
public sealed record ManifestExternalPackage(
    string Name,
    string? Version,
    string Ecosystem,
    string Scope);

/// <summary>
/// Represents a project extracted from a manifest file.
/// </summary>
public sealed record ManifestProjectDefinition
{
    /// <summary>Gets the human-readable project name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the primary language identifier for the project.</summary>
    public required string Language { get; init; }

    /// <summary>Gets the project path relative to the repository root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the repository-relative manifest path for the project, if known.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>Gets the declared external package dependencies.</summary>
    public IReadOnlyList<ManifestExternalPackage> ExternalPackages { get; init; } = [];

    /// <summary>
    /// Gets repository-relative project or manifest paths referenced by this project.
    /// </summary>
    public IReadOnlyList<string> ReferencedProjectPaths { get; init; } = [];
}

/// <summary>
/// Discovers manifests, dispatches them to language-specific parsers, and persists the resulting graph records.
/// </summary>
public sealed class ManifestParserOrchestrator
{
    private static readonly string[] IgnoredDirectoryNames = [".git", "bin", "obj", "node_modules"];
    private readonly IGraphStore graphStore;
    private readonly IReadOnlyList<IManifestParser> parsers;
    private readonly ILogger<ManifestParserOrchestrator> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestParserOrchestrator"/> class.
    /// </summary>
    /// <param name="graphStore">The graph store receiving project and dependency records.</param>
    /// <param name="parsers">The manifest parsers available for dispatch.</param>
    /// <param name="logger">The logger.</param>
    public ManifestParserOrchestrator(IGraphStore graphStore, IEnumerable<IManifestParser> parsers, ILogger<ManifestParserOrchestrator> logger)
    {
        this.graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        this.parsers = (parsers ?? throw new ArgumentNullException(nameof(parsers))).ToArray();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Discovers manifests under the repository root, parses them, and upserts the resulting graph records.
    /// </summary>
    /// <param name="repo">The repository to scan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onProgress">Optional callback for progress updates.</param>
    public async Task ParseAsync(Repo repo, CancellationToken cancellationToken = default, Action<string>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(repo);

        List<Project> projects = [];
        List<ExternalPackage> packages = [];
        List<(Project Project, IReadOnlyList<string> References)> pendingEdges = [];

        foreach (string manifestPath in EnumerateManifestPaths(repo.LocalPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string manifestRelativePath = NormalizePath(Path.GetRelativePath(repo.LocalPath, manifestPath));
            IManifestParser? parser = TryResolveParser(manifestRelativePath);
            if (parser is null)
            {
                continue;
            }

            ManifestParserContext context = new(repo, manifestPath, manifestRelativePath);
            IReadOnlyList<ManifestProjectDefinition> parsedProjects;
            try
            {
                parsedProjects = await parser.ParseAsync(context, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                this.logger.LogWarning(ex, "Skipping manifest '{ManifestPath}': invalid data.", manifestRelativePath);
                continue;
            }
            catch (XmlException ex)
            {
                this.logger.LogWarning(ex, "Skipping manifest '{ManifestPath}': XML parse error.", manifestRelativePath);
                continue;
            }

            onProgress?.Invoke($"Parsed manifest: {manifestRelativePath}");

            foreach (ManifestProjectDefinition parsedProject in parsedProjects)
            {
                Project project = new()
                {
                    Id = CreateDeterministicGuid("project", repo.Id.ToString("N"), NormalizePath(parsedProject.RelativePath)),
                    RepoId = repo.Id,
                    Language = parsedProject.Language,
                    Name = parsedProject.Name,
                    RelativePath = NormalizePath(parsedProject.RelativePath),
                    ManifestPath = parsedProject.ManifestPath is null
                        ? manifestRelativePath
                        : NormalizePath(parsedProject.ManifestPath),
                };

                await this.graphStore.UpsertProjectAsync(project, cancellationToken);
                projects.Add(project);
                pendingEdges.Add((project, parsedProject.ReferencedProjectPaths));

                foreach (ManifestExternalPackage package in parsedProject.ExternalPackages)
                {
                    packages.Add(new ExternalPackage
                    {
                        Id = CreateDeterministicGuid(
                            "package",
                            project.Id.ToString("N"),
                            package.Ecosystem,
                            package.Scope,
                            package.Name),
                        ProjectId = project.Id,
                        Name = package.Name,
                        Version = package.Version,
                        Ecosystem = package.Ecosystem,
                        Scope = package.Scope,
                    });
                }
            }
        }

        if (packages.Count > 0)
        {
            await this.graphStore.UpsertExternalPackageBatchAsync(packages, cancellationToken);
        }

        List<Edge> edges = BuildProjectDependencyEdges(projects, pendingEdges);
        if (edges.Count > 0)
        {
            await this.graphStore.UpsertEdgeBatchAsync(edges, cancellationToken);
        }
    }

    private static List<Edge> BuildProjectDependencyEdges(
        IReadOnlyList<Project> projects,
        IReadOnlyList<(Project Project, IReadOnlyList<string> References)> pendingEdges)
    {
        Dictionary<string, Project> projectLookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (Project project in projects)
        {
            projectLookup[NormalizePath(project.RelativePath)] = project;

            if (!string.IsNullOrWhiteSpace(project.ManifestPath))
            {
                projectLookup[NormalizePath(project.ManifestPath)] = project;
            }
        }

        List<Edge> edges = [];
        HashSet<(Guid SourceId, Guid TargetId)> seenEdges = [];

        foreach ((Project project, IReadOnlyList<string> references) in pendingEdges)
        {
            foreach (string reference in references)
            {
                string normalizedReference = NormalizePath(reference);
                if (!projectLookup.TryGetValue(normalizedReference, out Project? targetProject))
                {
                    continue;
                }

                if (!seenEdges.Add((project.Id, targetProject.Id)))
                {
                    continue;
                }

                edges.Add(new Edge
                {
                    Id = CreateDeterministicGuid("edge", project.Id.ToString("N"), targetProject.Id.ToString("N")),
                    SourceId = project.Id,
                    SourceKind = nameof(Project),
                    TargetId = targetProject.Id,
                    TargetKind = nameof(Project),
                    EdgeKind = EdgeKind.DependsOn,
                    Confidence = 1.0d,
                    Signals = [],
                    Properties = new Dictionary<string, object?>(),
                });
            }
        }

        return edges;
    }

    private static IEnumerable<string> EnumerateManifestPaths(string repoRoot)
    {
        IEnumerable<string> allFiles = Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories);
        foreach (string file in allFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = NormalizePath(Path.GetRelativePath(repoRoot, file));
            if (IsIgnored(relativePath))
            {
                continue;
            }

            yield return file;
        }
    }

    private static bool IsIgnored(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        return segments.Any(segment => IgnoredDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static Guid CreateDeterministicGuid(params string[] parts)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        byte[] guidBytes = bytes[..16];
        return new Guid(guidBytes);
    }

    private IManifestParser? TryResolveParser(string manifestRelativePath)
    {
        IManifestParser[] matchingParsers = this.parsers.Where(parser => parser.CanParse(manifestRelativePath)).ToArray();
        return matchingParsers.Length switch
        {
            1 => matchingParsers[0],
            > 1 => throw new InvalidOperationException(
                $"Multiple manifest parsers matched '{manifestRelativePath}'."),
            _ => null,
        };
    }
}
