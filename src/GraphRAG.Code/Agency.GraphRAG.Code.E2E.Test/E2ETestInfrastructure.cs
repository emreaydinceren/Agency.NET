using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Manifest;
using Agency.GraphRAG.Code.Postgres;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Sqlite;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Walker;
using Agency.Sql.Postgre;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;
using ModuleRecord = Agency.GraphRAG.Code.Domain.Module;

namespace Agency.GraphRAG.Code.E2E.Test;

internal static partial class E2ETestInfrastructure
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string WorkingRoot { get; } = Path.Combine(RepoRoot, "src", "obj", "GraphRAG.Code.E2E");

    public static bool UseMockLlm =>
        !string.Equals(Environment.GetEnvironmentVariable("AGENCY_E2E_MOCK_LLM"), "0", StringComparison.Ordinal);

    public static string GetHeadCommit(string repoRoot) => RunGit(repoRoot, "rev-parse HEAD");

    public static string CreateScratchDirectory()
    {
        Directory.CreateDirectory(WorkingRoot);
        string path = Path.Combine(WorkingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static SqliteHarness CreateSqliteHarness()
    {
        string scratchDirectory = CreateScratchDirectory();
        string databasePath = Path.Combine(scratchDirectory, "agency-repo.sqlite");
        var runner = new SqliteRunner($"Data Source={databasePath}");
        var embeddingGenerator = new FakeEmbeddingGenerator();
        var store = new SqliteGraphStore(runner, embeddingGenerator, NullLogger<SqliteGraphStore>.Instance);
        return new SqliteHarness(scratchDirectory, databasePath, runner, embeddingGenerator, store);
    }

    public static PostgresHarness CreatePostgresHarness()
    {
        string baseConnectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings:PostgreSql")
            ?? "Host=localhost;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db";

        string schema = $"graphrag_code_e2e_{Guid.NewGuid():N}";
        string connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = $"{schema},public",
            Options = $"-c search_path={schema},public",
        }.ConnectionString;

        var rootRunner = new PostgreSqlRunner(baseConnectionString);
        var runner = new PostgreSqlRunner(connectionString);
        var embeddingGenerator = new FakeEmbeddingGenerator();
        var store = new PostgresGraphStore(runner, embeddingGenerator, connectionString, NullLogger<PostgresGraphStore>.Instance);
        return new PostgresHarness(schema, rootRunner, runner, embeddingGenerator, store);
    }

    public static QueryPipeline CreateQueryPipeline(
        IGraphStore store,
        MockChatClient chatClient,
        ISymbolTextProvider symbolTextProvider,
        IClusterQuerySource clusterSource)
        => new(
            new QueryPlanner(new QueryClassifier(chatClient, new QueryOptions { CheapestModel = "mock-cheap", AnswerModel = "mock-answer", ContextTokenBudget = 1200 })),
            new HybridRetriever(store, clusterSource, symbolTextProvider),
            new ContextAssembler(),
            chatClient,
            new QueryOptions
            {
                CheapestModel = "mock-cheap",
                AnswerModel = "mock-answer",
                ContextTokenBudget = 1200,
            });

    public static string CreateWorktree(string repoRoot)
    {
        string worktreePath = Path.Combine(CreateScratchDirectory(), "worktree");
        _ = RunGit(repoRoot, $"worktree add --detach \"{worktreePath}\" HEAD");
        _ = RunGit(worktreePath, "config user.name \"Copilot E2E\"");
        _ = RunGit(worktreePath, "config user.email \"copilot-e2e@example.com\"");
        return worktreePath;
    }

    public static void RemoveWorktree(string repoRoot, string worktreePath)
    {
        if (!Directory.Exists(worktreePath))
        {
            return;
        }

        _ = RunGit(repoRoot, $"worktree remove --force \"{worktreePath}\"");
    }

    public static void TouchChatSessionForIncrementalTest(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "src", "Agentic", "Agency.Agentic", "ChatSession.cs");
        string original = File.ReadAllText(path);
        const string marker = "        this._ctx ??= Agent.CreateContext(userMessage, this._toolContext);";
        string updated = original.Replace(
            marker,
            "        // e2e incremental reindex marker" + Environment.NewLine + marker,
            StringComparison.Ordinal);

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Could not update ChatSession.cs for the incremental reindex test.");
        }

        File.WriteAllText(path, updated);
        _ = RunGit(repoRoot, "add \"src/Agentic/Agency.Agentic/ChatSession.cs\"");
        _ = RunGit(repoRoot, "commit -m \"E2E incremental reindex\" --no-gpg-sign");
    }

    public static string ReadSymbolHashSnapshotSql => """
        SELECT f.path, s.id, s.content_hash, COALESCE(hex(s.embedding), '')
        FROM symbols AS s
        JOIN files AS f ON f.id = s.file_id
        ORDER BY f.path ASC, s.id ASC;
        """;

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "src", "Agency.slnx")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root containing src\\Agency.slnx.");
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{workingDirectory}\" {arguments}",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}:{Environment.NewLine}{stdout}{stderr}");
        }

        return stdout.Trim();
    }
}

internal sealed class SqliteHarness(
    string scratchDirectory,
    string databasePath,
    SqliteRunner runner,
    FakeEmbeddingGenerator embeddingGenerator,
    SqliteGraphStore store) : IAsyncDisposable
{
    public string ScratchDirectory { get; } = scratchDirectory;

    public string DatabasePath { get; } = databasePath;

    public SqliteRunner Runner { get; } = runner;

    public FakeEmbeddingGenerator EmbeddingGenerator { get; } = embeddingGenerator;

    public SqliteGraphStore Store { get; } = store;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(this.ScratchDirectory))
            {
                Directory.Delete(this.ScratchDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class PostgresHarness(
    string schema,
    PostgreSqlRunner rootRunner,
    PostgreSqlRunner runner,
    FakeEmbeddingGenerator embeddingGenerator,
    PostgresGraphStore store) : IAsyncDisposable
{
    public string Schema { get; } = schema;

    public PostgreSqlRunner RootRunner { get; } = rootRunner;

    public PostgreSqlRunner Runner { get; } = runner;

    public FakeEmbeddingGenerator EmbeddingGenerator { get; } = embeddingGenerator;

    public PostgresGraphStore Store { get; } = store;

    public async ValueTask InitializeAsync()
    {
        await this.RootRunner.ExecuteAsync($"""CREATE SCHEMA "{this.Schema}";""", cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.RootRunner.ExecuteAsync($"""DROP SCHEMA IF EXISTS "{this.Schema}" CASCADE;""", cancellationToken: CancellationToken.None);
        }
        finally
        {
            await this.Runner.DisposeAsync();
            await this.RootRunner.DisposeAsync();
        }
    }
}

internal sealed class AgencyRepoIndexer(IGraphStore store, FakeEmbeddingGenerator embeddingGenerator)
{
    private readonly IGraphStore _store = store;
    private readonly FakeEmbeddingGenerator _embeddingGenerator = embeddingGenerator;
    private readonly Phase1Writer _phase1Writer = new(store);
    private readonly CSharpManifestParser _manifestParser = new();

    public async Task<IndexArtifacts> IndexAsync(Repo repo, CancellationToken cancellationToken = default)
    {
        await _store.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _store.UpsertRepoAsync(repo, cancellationToken).ConfigureAwait(false);

        ManifestParserOrchestrator manifestOrchestrator = new(_store, [new CSharpManifestParser(), new NpmManifestParser(), new PythonManifestParser()]);
        await manifestOrchestrator.ParseAsync(repo, cancellationToken).ConfigureAwait(false);

        RepoWalker walker = new(new GitProcessRunner());
        WalkResult walkResult = await walker.WalkAsync(repo, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, Project> projectsByDirectory = LoadProjects(repo);

        List<SimpleParsedFile> parsedFiles = [];
        foreach (WalkedFile walkedFile in walkResult.Files.Where(static file => file.Language is Language.CSharp))
        {
            string relativePath = walkedFile.Path.Replace('/', '\\');
            Guid fileId = StableGuid("file", relativePath);

            if (walkedFile.Status is WalkedFileStatus.Deleted)
            {
                await _store.DeleteFileAsync(fileId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (walkedFile.Status is WalkedFileStatus.Modified)
            {
                await _store.DeleteSymbolsByFileAsync(fileId, cancellationToken).ConfigureAwait(false);
            }

            if (!projectsByDirectory.TryGetValue(FindProjectDirectory(relativePath, projectsByDirectory.Keys), out Project? project))
            {
                continue;
            }

            string absolutePath = Path.Combine(repo.LocalPath, relativePath);
            string source = File.ReadAllText(absolutePath);
            SimpleParsedFile parsed = SimpleCSharpIndexer.Parse(repo, project, relativePath, source, _embeddingGenerator);
            parsedFiles.Add(parsed);
            await _phase1Writer.WriteAsync(parsed.ToWriteRequest(), cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ClusterRecord> clusters = CreateClusters(parsedFiles);
        if (clusters.Count > 0)
        {
            await _store.ReplaceClusterSummariesAtomicallyAsync(clusters, cancellationToken).ConfigureAwait(false);
        }

        string? indexedCommit = null;
        if (!string.IsNullOrWhiteSpace(walkResult.HeadCommit))
        {
            await _store.SetIndexedCommitAsync(repo.Id, walkResult.HeadCommit, cancellationToken).ConfigureAwait(false);
            indexedCommit = walkResult.HeadCommit;
        }

        return new IndexArtifacts(parsedFiles, clusters, indexedCommit);
    }

    private static IReadOnlyDictionary<string, Project> LoadProjects(Repo repo)
    {
        Dictionary<string, Project> projects = new(StringComparer.OrdinalIgnoreCase);

        foreach (string manifestPath in Directory.EnumerateFiles(repo.LocalPath, "*.csproj", SearchOption.AllDirectories))
        {
            ManifestParseResult parsed = new CSharpManifestParser().Parse(repo.LocalPath, manifestPath);
            string relativeProjectPath = parsed.ProjectRelativePath.Replace('/', '\\');
            string relativeManifestPath = parsed.ManifestRelativePath.Replace('/', '\\');
            projects[relativeProjectPath] = new Project
            {
                Id = StableGuid("project", repo.Id.ToString("N"), relativeProjectPath),
                RepoId = repo.Id,
                Name = parsed.ProjectName,
                RelativePath = relativeProjectPath,
                ManifestPath = relativeManifestPath,
                Language = "csharp",
            };
        }

        return projects;
    }

    private static string FindProjectDirectory(string relativePath, IEnumerable<string> projectDirectories)
    {
        string? match = projectDirectories
            .Where(directory => relativePath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relativePath, directory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static directory => directory.Length)
            .FirstOrDefault();

        return match ?? string.Empty;
    }

    private static IReadOnlyList<ClusterRecord> CreateClusters(IReadOnlyList<SimpleParsedFile> parsedFiles)
    {
        List<SimpleParsedSymbol> businessSymbols = parsedFiles
            .SelectMany(static file => file.Symbols)
            .Where(static symbol => AgencyRepoExpectations.ChatAgentSymbols.Contains(symbol.Symbol.Name, StringComparer.Ordinal))
            .ToList();
        List<SimpleParsedSymbol> infrastructureSymbols = parsedFiles
            .SelectMany(static file => file.Symbols)
            .Where(static symbol => AgencyRepoExpectations.LlmClientSymbols.Contains(symbol.Symbol.Name, StringComparer.Ordinal))
            .ToList();

        List<ClusterRecord> clusters = [];
        if (businessSymbols.Count > 0)
        {
            clusters.Add(new ClusterRecord
            {
                Id = StableGuid("cluster", "business", "agentic"),
                Label = "business: agentic chat flow",
                Summary = "Agent, ChatSession, IConversationManager, InMemoryConversationManager, and SystemPromptBuilder form the chat flow.",
                Type = ClusterType.Business,
                CoherenceScore = 0.9,
                Embedding = FakeEmbeddingGenerator.CreateVector("business agentic chat flow"),
            });
        }

        if (infrastructureSymbols.Count > 0)
        {
            clusters.Add(new ClusterRecord
            {
                Id = StableGuid("cluster", "infrastructure", "llm"),
                Label = "infrastructure: llm providers",
                Summary = "ClaudeClient and OpenAIClient integrate external LLM provider SDKs.",
                Type = ClusterType.Infrastructure,
                CoherenceScore = 0.8,
                Embedding = FakeEmbeddingGenerator.CreateVector("infrastructure llm providers"),
            });
        }

        return clusters;
    }

    private static Guid StableGuid(params string[] parts)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return new Guid(bytes[..16]);
    }
}

internal sealed record IndexArtifacts(
    IReadOnlyList<SimpleParsedFile> Files,
    IReadOnlyList<ClusterRecord> Clusters,
    string? IndexedCommit);

internal sealed record SimpleParsedFile(
    SourceFile File,
    ModuleRecord? Module,
    IReadOnlyList<SimpleParsedSymbol> Symbols)
{
    public Phase1WriteRequest ToWriteRequest() =>
        new(
            this.File,
            this.Module,
            this.Symbols
                .GroupBy(static symbol => symbol.Chunk.Id, StringComparer.Ordinal)
                .Select(static group => group.First().Chunk)
                .ToArray(),
            this.Symbols
                .GroupBy(static symbol => symbol.Chunk.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First().Summary, StringComparer.Ordinal),
            []);
}

internal sealed record SimpleParsedSymbol(
    Agency.GraphRAG.Code.Chunker.Chunk Chunk,
    Symbol Symbol,
    Agency.GraphRAG.Code.Summarizer.SymbolSummary Summary);

internal sealed partial class SimpleCSharpIndexer
{
    private static readonly Regex NamespaceRegex = CreateNamespaceRegex();
    private static readonly Regex TypeRegex = CreateTypeRegex();
    private static readonly Regex MethodRegex = CreateMethodRegex();
    private static readonly Regex PropertyRegex = CreatePropertyRegex();

    public static SimpleParsedFile Parse(Repo repo, Project project, string relativePath, string source, FakeEmbeddingGenerator embeddingGenerator)
    {
        string[] lines = source.ReplaceLineEndings("\n").Split('\n');
        string currentNamespace = string.Empty;
        Stack<(string Name, string FullyQualifiedName, int BraceDepth)> typeStack = new();
        int braceDepth = 0;
        List<SimpleParsedSymbol> symbols = [];

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();

            Match namespaceMatch = NamespaceRegex.Match(trimmed);
            if (namespaceMatch.Success)
            {
                currentNamespace = namespaceMatch.Groups["name"].Value;
                symbols.Add(CreateSymbol(relativePath, lines, index, index, namespaceMatch.Groups["name"].Value, currentNamespace, SymbolKind.Namespace, null, null, embeddingGenerator));
            }

            Match typeMatch = TypeRegex.Match(trimmed);
            if (typeMatch.Success)
            {
                string name = typeMatch.Groups["name"].Value;
                string fullyQualifiedName = string.IsNullOrWhiteSpace(currentNamespace)
                    ? name
                    : $"{currentNamespace}.{string.Join(".", typeStack.Reverse().Select(static frame => frame.Name).Append(name))}";
                SymbolKind kind = typeMatch.Groups["kind"].Value switch
                {
                    "interface" => SymbolKind.Interface,
                    "struct" => SymbolKind.Struct,
                    "enum" => SymbolKind.Enum,
                    _ => SymbolKind.Class,
                };
                int endLine = FindBlockEnd(lines, index);
                symbols.Add(CreateSymbol(relativePath, lines, index, endLine, name, fullyQualifiedName, kind, null, trimmed, embeddingGenerator));
                typeStack.Push((name, fullyQualifiedName, braceDepth + CountOpenBraces(line) - CountCloseBraces(line)));
            }
            else if (typeStack.Count > 0)
            {
                Match methodMatch = MethodRegex.Match(trimmed);
                if (methodMatch.Success)
                {
                    string methodName = methodMatch.Groups["name"].Value;
                    if (!IsControlKeyword(methodName))
                    {
                        string parent = typeStack.Peek().FullyQualifiedName;
                        int endLine = FindBlockEnd(lines, index);
                        symbols.Add(CreateSymbol(relativePath, lines, index, endLine, methodName, $"{parent}.{methodName}", SymbolKind.Method, null, trimmed, embeddingGenerator));
                    }
                }

                Match propertyMatch = PropertyRegex.Match(trimmed);
                if (propertyMatch.Success)
                {
                    string propertyName = propertyMatch.Groups["name"].Value;
                    string parent = typeStack.Peek().FullyQualifiedName;
                    symbols.Add(CreateSymbol(relativePath, lines, index, index, propertyName, $"{parent}.{propertyName}", SymbolKind.Property, null, trimmed, embeddingGenerator));
                }
            }

            braceDepth += CountOpenBraces(line) - CountCloseBraces(line);
            while (typeStack.Count > 0 && braceDepth < typeStack.Peek().BraceDepth)
            {
                _ = typeStack.Pop();
            }
        }

        SourceFile file = new()
        {
            Id = StableGuid("file", relativePath),
            RepoId = repo.Id,
            ProjectId = project.Id,
            Path = relativePath,
            Language = "csharp",
            ContentHash = ComputeHash(source),
        };

        ModuleRecord? module = null;
        if (!string.IsNullOrWhiteSpace(currentNamespace))
        {
            module = new ModuleRecord
            {
                Id = StableGuid("module", relativePath, currentNamespace),
                FileId = file.Id,
                Name = currentNamespace,
                Kind = "namespace",
            };
        }

        return new SimpleParsedFile(file, module, symbols);
    }

    private static SimpleParsedSymbol CreateSymbol(
        string relativePath,
        string[] lines,
        int startLine,
        int endLine,
        string name,
        string fullyQualifiedName,
        SymbolKind kind,
        string? parentId,
        string? signature,
        FakeEmbeddingGenerator embeddingGenerator)
    {
        string content = string.Join(Environment.NewLine, lines[startLine..(Math.Min(endLine + 1, lines.Length))]);
        Agency.GraphRAG.Code.Chunker.Chunk chunk = Agency.GraphRAG.Code.Chunker.ChunkBuilder.Build(
            relativePath,
            Language.CSharp,
            kind switch
            {
                SymbolKind.Namespace => Agency.GraphRAG.Code.Chunker.ChunkGranularity.Namespace,
                SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum => Agency.GraphRAG.Code.Chunker.ChunkGranularity.Type,
                _ => Agency.GraphRAG.Code.Chunker.ChunkGranularity.Member,
            },
            name,
            fullyQualifiedName,
            signature,
            content,
            new Agency.GraphRAG.Code.Chunker.ChunkSourceRange(startLine + 1, 1, endLine + 1, Math.Max(1, lines[endLine].Length)),
            kind,
            [],
            parentId);
        Agency.GraphRAG.Code.Summarizer.SymbolSummary summary = new(
            $"{fullyQualifiedName} in {Path.GetFileName(relativePath)}",
            content.Trim(),
            [],
            embeddingGenerator.GenerateEmbeddingAsync(content).GetAwaiter().GetResult());
        Symbol symbol = new()
        {
            Id = StableGuid("symbol", chunk.Id),
            FileId = StableGuid("file", relativePath),
            ModuleId = null,
            Name = name,
            FullyQualifiedName = fullyQualifiedName,
            Kind = kind,
            Signature = signature,
            Summary = summary.Detailed,
            OneLineSummary = summary.OneLine,
            ContentHash = ComputeHash(content),
            Embedding = summary.OneLineEmbedding.ToArray(),
            IsUtility = false,
            SourceRangeStart = startLine + 1,
            SourceRangeEnd = endLine + 1,
        };

        return new SimpleParsedSymbol(chunk, symbol, summary);
    }

    private static int FindBlockEnd(string[] lines, int startLine)
    {
        int depth = 0;
        bool seenOpenBrace = false;

        for (int index = startLine; index < lines.Length; index++)
        {
            string line = lines[index];
            depth += CountOpenBraces(line);
            if (CountOpenBraces(line) > 0)
            {
                seenOpenBrace = true;
            }

            depth -= CountCloseBraces(line);
            if (seenOpenBrace && depth <= 0)
            {
                return index;
            }
        }

        return startLine;
    }

    private static bool IsControlKeyword(string name) =>
        name is "if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "lock" or "return";

    private static int CountOpenBraces(string line) => line.Count(static character => character == '{');

    private static int CountCloseBraces(string line) => line.Count(static character => character == '}');

    private static Guid StableGuid(params string[] parts)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return new Guid(bytes[..16]);
    }

    private static string ComputeHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"^namespace\s+(?<name>[A-Za-z0-9_.]+)")]
    private static partial Regex CreateNamespaceRegex();

    [GeneratedRegex(@"(?<kind>class|interface|struct|enum|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex CreateTypeRegex();

    [GeneratedRegex(@"(?:public|private|internal|protected|static|virtual|override|sealed|abstract|async|partial|new|\s)+[\w<>\[\],?.]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex CreateMethodRegex();

    [GeneratedRegex(@"(?:public|private|internal|protected|static|virtual|override|sealed|abstract|new|\s)+[\w<>\[\],?.]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{")]
    private static partial Regex CreatePropertyRegex();
}

internal sealed class SymbolTextProvider(IReadOnlyDictionary<Guid, string> texts) : ISymbolTextProvider
{
    public Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.TryGetValue(symbol.Id, out string? text) ? text : null);
}

internal sealed class InMemoryClusterSource(IReadOnlyList<ClusterRecord> clusters) : IClusterQuerySource
{
    public Task<IReadOnlyList<ClusterRecord>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClusterRecord>>(clusters.Where(cluster => clusterIds.Contains(cluster.Id)).ToArray());
}

internal sealed class MockChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("AgencyRepoMockChatClient", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string prompt = string.Concat(messages.SelectMany(static message => message.Contents.OfType<TextContent>()).Select(static content => content.Text));
        string text = prompt.Contains("Choose exactly one category", StringComparison.Ordinal)
            ? Classify(prompt)
            : Answer(prompt);
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? key = null) => null;

    public void Dispose()
    {
    }

    private static string Classify(string prompt)
    {
        string query = ExtractQuestion(prompt);
        return query switch
        {
            "How does chat with agent work?" => nameof(QueryCategory.Subsystem),
            "What does Agency.Llm.Claude depend on?" => nameof(QueryCategory.Dependency),
            "What calls IConversationManager?" => nameof(QueryCategory.Impact),
            "Give me a tour of the codebase." => nameof(QueryCategory.Global),
            "What does ChatSession.SendAsync do?" => nameof(QueryCategory.Local),
            _ => nameof(QueryCategory.Local),
        };
    }

    private static string Answer(string prompt)
    {
        string query = ExtractQuestion(prompt);
        return query switch
        {
            "How does chat with agent work?" =>
                "Agent drives the loop, ChatSession keeps turn state, IConversationManager stores messages, InMemoryConversationManager is the default implementation, and SystemPromptBuilder rebuilds the system prompt on each turn.",
            "What does Agency.Llm.Claude depend on?" =>
                "Agency.Llm.Claude wraps the Anthropic SDK and depends on the Anthropic package for Claude API access.",
            "What calls IConversationManager?" =>
                "InMemoryConversationManager implements IConversationManager, and Context consumes IConversationManager through its Conversation property inside Agency.Agentic.",
            "Give me a tour of the codebase." =>
                "business: Agentic chat flow and GraphRAG indexing are the main business clusters." + Environment.NewLine + Environment.NewLine +
                "Infrastructure footer: LLM providers, SQL runners, and embeddings infrastructure support the business layers.",
            "What does ChatSession.SendAsync do?" =>
                "ChatSession.SendAsync lazily creates the agent context, streams Agent.ChatAsync events, and increments TurnCount when an AgentResultEvent completes the turn.",
            _ => "No mock answer is configured for this question.",
        };
    }

    private static string ExtractQuestion(string prompt)
    {
        string normalized = prompt.ReplaceLineEndings("\n");
        const string queryMarker = "Query:\n";
        int queryIndex = normalized.LastIndexOf(queryMarker, StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            return normalized[(queryIndex + queryMarker.Length)..].Trim();
        }

        const string questionMarker = "Question:\n";
        int questionIndex = normalized.IndexOf(questionMarker, StringComparison.Ordinal);
        if (questionIndex >= 0)
        {
            string remaining = normalized[(questionIndex + questionMarker.Length)..];
            int contextIndex = remaining.IndexOf("\n\nContext:\n", StringComparison.Ordinal);
            return (contextIndex >= 0 ? remaining[..contextIndex] : remaining).Trim();
        }

        return normalized.Trim();
    }
}

internal sealed class FakeEmbeddingGenerator : Agency.Embeddings.Common.IEmbeddingGenerator
{
    public const int Dimensions = 1536;

    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReadOnlyMemory<float>(CreateVector(input)));

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(input => new ReadOnlyMemory<float>(CreateVector(input))).ToArray());

    public static float[] CreateVector(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        float[] vector = new float[Dimensions];
        if (bytes.Length == 0)
        {
            return vector;
        }

        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = bytes[index % bytes.Length] / 255f;
        }

        return vector;
    }
}
