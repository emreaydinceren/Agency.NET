using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.DependencyInjection;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Sqlite;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.GraphRAG.Code.E2E.Test;

/// <summary>
/// End-to-end SQLite indexing tests against the Agency repository.
/// </summary>
[Trait("Category", "Functional")]
public sealed class SqliteFullIndexTests : IAsyncLifetime
{
    private SqliteHarness? _harness;
    private Repo? _repo;

    public async ValueTask InitializeAsync()
    {
        this._harness = E2ETestInfrastructure.CreateSqliteHarness();
        this._repo = new Repo
        {
            Id = Guid.Parse("a6260d5f-2f6f-4312-b40f-9d2d1d4535f8"),
            LocalPath = E2ETestInfrastructure.RepoRoot,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };

        AgencyRepoIndexer indexer = new(this._harness.Store, this._harness.EmbeddingGenerator);
        IndexArtifacts artifacts = await indexer.IndexAsync(this._repo, TestContext.Current.CancellationToken);
        this._repo = this._repo with { IndexedCommit = artifacts.IndexedCommit };
    }

    public async ValueTask DisposeAsync()
    {
        if (this._harness is not null)
        {
            await this._harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task FullIndex_PopulatesExpectedAgencySymbolsAndCheckpoint()
    {
        Assert.NotNull(this._harness);
        Assert.NotNull(this._repo);

        IReadOnlyList<long> counts = await this._harness!.Runner.QueryAsync(
            "SELECT COUNT(*) FROM symbols;",
            reader => Task.FromResult(reader.GetInt64(0)),
            cancellationToken: TestContext.Current.CancellationToken);
        long symbolCount = Assert.Single(counts);
        Assert.InRange(symbolCount, 200, 50000);

        foreach (string symbolName in AgencyRepoExpectations.ChatAgentSymbols.Concat(AgencyRepoExpectations.LlmClientSymbols))
        {
            IReadOnlyList<Symbol> matches = await this._harness.Store.FindSymbolsByNameAsync(symbolName, TestContext.Current.CancellationToken);
            Assert.NotEmpty(matches);
        }

        string? indexedCommit = await this._harness.Store.LoadIndexedCommitAsync(this._repo!.Id, TestContext.Current.CancellationToken);
        Assert.Equal(E2ETestInfrastructure.GetHeadCommit(E2ETestInfrastructure.RepoRoot), indexedCommit);
    }
}

/// <summary>
/// Integration test for the full indexing pipeline using AddCodeIndex() DI setup.
/// </summary>
[Trait("Category", "Functional")]
public sealed class FullIndexPipelineTests : IAsyncLifetime
{
    private FullIndexPipelineFixture? _fixture;

    public async ValueTask InitializeAsync()
    {
        this._fixture = new FullIndexPipelineFixture();
        await this._fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (this._fixture is not null)
        {
            await this._fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Tests that the full indexing pipeline creates symbols and files from a minimal fixture repository.
    /// </summary>
    [Fact]
    public async Task FullIndexPipeline_CreatesSymbolsAndFiles()
    {
        Assert.NotNull(this._fixture);

        if (this._fixture.SkipReason is not null)
        {
            Assert.Skip(this._fixture.SkipReason);
        }

        Assert.NotNull(this._fixture.Repo);

        var store = this._fixture.Store;
        var repo = this._fixture.Repo;

        SourceFile? indexedFile = await store.GetFileByPathAsync("Program.cs", TestContext.Current.CancellationToken);
        Assert.NotNull(indexedFile);
        Assert.Equal("Program.cs", indexedFile.Path);

        IReadOnlyList<Symbol> symbols = await store.FindSymbolsByNameAsync("HelloWorld", TestContext.Current.CancellationToken);
        Assert.NotEmpty(symbols);

        var helloWorldSymbol = symbols.FirstOrDefault(s => s.Name == "HelloWorld");
        Assert.NotNull(helloWorldSymbol);
        Assert.Equal("HelloWorld.Program.HelloWorld", helloWorldSymbol.FullyQualifiedName);

        string? indexedCommit = await store.LoadIndexedCommitAsync(repo.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(indexedCommit);
        Assert.NotEmpty(indexedCommit);
    }
}

/// <summary>
/// Fixture for the full indexing pipeline integration test.
/// </summary>
internal sealed class FullIndexPipelineFixture : IAsyncDisposable
{
    private readonly string _repoDirectory;
    private readonly string _databasePath;
    private SqliteRunner? _runner;
    private SqliteGraphStore? _store;
    private string? _skipReason;

    public Repo Repo { get; private set; }

    public IGraphStore Store
    {
        get => this._store ?? throw new InvalidOperationException("Store not initialized.");
    }

    public string? SkipReason => this._skipReason;

    public FullIndexPipelineFixture()
    {
        string scratchDir = E2ETestInfrastructure.CreateScratchDirectory();
        this._repoDirectory = Path.Combine(scratchDir, "test-repo");
        this._databasePath = Path.Combine(scratchDir, "index.sqlite");
        this.Repo = new Repo
        {
            Id = Guid.NewGuid(),
            LocalPath = this._repoDirectory,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };
    }

    /// <summary>
    /// Initializes the test fixture by creating a minimal C# repository and indexing it.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(this._repoDirectory);

            string projectFile = Path.Combine(this._repoDirectory, "TestProject.csproj");
            File.WriteAllText(projectFile, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            string programFile = Path.Combine(this._repoDirectory, "Program.cs");
            File.WriteAllText(programFile, """
                namespace HelloWorld
                {
                    public static class Program
                    {
                        public static void HelloWorld()
                        {
                            System.Console.WriteLine("Hello, World!");
                        }
                    }
                }
                """);

            E2ETestInfrastructure.RunGit(this._repoDirectory, "init");
            E2ETestInfrastructure.RunGit(this._repoDirectory, "config user.name \"Test User\"");
            E2ETestInfrastructure.RunGit(this._repoDirectory, "config user.email \"test@example.com\"");
            E2ETestInfrastructure.RunGit(this._repoDirectory, "add .");
            E2ETestInfrastructure.RunGit(this._repoDirectory, "commit -m \"Initial commit\" --no-gpg-sign");

            this._runner = new SqliteRunner($"Data Source={this._databasePath}");
            var embeddingGenerator = new FakeEmbeddingGenerator();
            this._store = new SqliteGraphStore(this._runner, embeddingGenerator, FakeEmbeddingGenerator.Dimensions, NullLogger<SqliteGraphStore>.Instance);

            await this._store.InitializeSchemaAsync(CancellationToken.None);
            await this._store.UpsertRepoAsync(this.Repo, CancellationToken.None);

            var services = new ServiceCollection();
            services.AddCodeIndex(options =>
            {
                options.Store = CodeIndexStore.Sqlite;
                options.SqlitePath = this._databasePath;
            });

            var serviceProvider = services.BuildServiceProvider();
            var pipeline = serviceProvider.GetRequiredService<IndexingPipeline>();

            await pipeline.RunAsync(this.Repo, CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Tree-sitter"))
        {
            this._skipReason = "Tree-sitter assembly not available. Ensure Agency.GraphRAG.Code.TreeSitter is loaded. This may be expected in environments without the TreeSitter pipeline compiled.";
        }
    }

    /// <summary>
    /// Cleans up the temporary repository and database.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(this._repoDirectory))
            {
                Directory.Delete(this._repoDirectory, recursive: true);
            }

            if (File.Exists(this._databasePath))
            {
                File.Delete(this._databasePath);
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
