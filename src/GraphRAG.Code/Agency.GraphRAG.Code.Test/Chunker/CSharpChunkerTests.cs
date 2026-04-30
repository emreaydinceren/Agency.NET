using System.Diagnostics;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Chunker;

/// <summary>
/// Tests for <see cref="CSharpChunker"/>.
/// </summary>
public sealed class CSharpChunkerTests
{
    [Fact]
    public async Task Chunk_SimpleFixture_EmitsNamespaceClassAndMethodHierarchy()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Simple.cs");

        Chunk namespaceChunk = Assert.Single(chunks, chunk => chunk.SymbolKind == SymbolKind.Namespace);
        Assert.Equal("Sample.Simple", namespaceChunk.FullyQualifiedName);
        Assert.Equal(ChunkGranularity.Namespace, namespaceChunk.Granularity);
        Assert.Equal(new ChunkSourceRange(3, 0, 17, 1), namespaceChunk.Range);

        Chunk classChunk = Assert.Single(chunks, chunk => chunk.SymbolKind == SymbolKind.Class);
        Assert.Equal(namespaceChunk.Id, classChunk.ParentId);
        Assert.Equal(ChunkGranularity.Type, classChunk.Granularity);
        Assert.Equal("Sample.Simple.Worker", classChunk.FullyQualifiedName);
        Assert.Equal(new ChunkSourceRange(5, 4, 16, 5), classChunk.Range);
        Assert.Equal(["System", "System.Threading.Tasks"], classChunk.ImportsInScope.Select(import => import.Source).ToArray());

        Chunk[] methods = chunks.Where(chunk => chunk.SymbolKind == SymbolKind.Method).OrderBy(chunk => chunk.Name).ToArray();
        Assert.Equal(2, methods.Length);
        Assert.All(methods, method => Assert.Equal(classChunk.Id, method.ParentId));
        Assert.Equal(["Run", "StopAsync"], methods.Select(method => method.Name).ToArray());
        Assert.Equal(new ChunkSourceRange(7, 8, 10, 9), methods[0].Range);
        Assert.Equal(new ChunkSourceRange(12, 8, 15, 9), methods[1].Range);

        await AssertCommonInvariantsAsync("Simple.cs", chunks, ["System", "System.Threading.Tasks"]);
    }

    [Fact]
    public async Task Chunk_InterfaceFixture_ExtractsInheritsAndImplements()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Interface.cs");

        Chunk interfaceChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Sample.Contracts.IWorker");
        Assert.Equal(SymbolKind.Interface, interfaceChunk.SymbolKind);
        Assert.Equal(["IDisposable"], interfaceChunk.Inherits);

        Chunk abstractClassChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Sample.Contracts.WorkerBase");
        Assert.Equal(SymbolKind.Class, abstractClassChunk.SymbolKind);
        Assert.Empty(abstractClassChunk.Inherits ?? []);
        Assert.Equal(["IWorker"], abstractClassChunk.Implements);

        Chunk concreteChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Sample.Contracts.ConcreteWorker");
        Assert.Equal(["WorkerBase"], concreteChunk.Inherits);
        Assert.Equal(["IWorker"], concreteChunk.Implements);

        await AssertCommonInvariantsAsync("Interface.cs", chunks, ["System"]);
    }

    [Fact]
    public async Task Chunk_RecordFixture_CapturesPrimaryConstructorAsMember()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Records.cs");

        Chunk recordChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Sample.Models.Person");
        Assert.Equal(SymbolKind.Class, recordChunk.SymbolKind);

        Chunk constructorChunk = Assert.Single(chunks, chunk => chunk.ParentId == recordChunk.Id && chunk.Name == "Person");
        Assert.Equal(SymbolKind.Method, constructorChunk.SymbolKind);
        Assert.Equal("Person(string Name, int Age)", constructorChunk.Signature);
        Assert.Equal(new ChunkSourceRange(2, 26, 2, 48), constructorChunk.Range);

        await AssertCommonInvariantsAsync("Records.cs", chunks, []);
    }

    [Fact]
    public async Task Chunk_LargeMethodFixture_EmitsStatementFallbackChunks()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("LargeMethod.cs", new ChunkerOptions { MaxChunkChars = 80 });

        Chunk methodChunk = Assert.Single(chunks, chunk => chunk.SymbolKind == SymbolKind.Method && chunk.Name == "Run");
        Chunk[] statementChunks = chunks.Where(chunk => chunk.Granularity == ChunkGranularity.Statement).OrderBy(chunk => chunk.Name).ToArray();

        Assert.True(statementChunks.Length >= 5);
        Assert.All(statementChunks, chunk => Assert.Equal(methodChunk.Id, chunk.ParentId));
        Assert.Contains(statementChunks, chunk => chunk.Content.Contains("counter += 1;", StringComparison.Ordinal));
        Assert.Contains(statementChunks, chunk => chunk.Content.Contains("if (counter > 0)", StringComparison.Ordinal));

        await AssertCommonInvariantsAsync("LargeMethod.cs", chunks, ["System"], new ChunkerOptions { MaxChunkChars = 80 });
    }

    [Fact]
    public async Task Chunk_GenericsFixture_CapturesGenericSignatures()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Generics.cs");

        Chunk classChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Sample.Generics.Repository");
        Assert.Contains("<TKey, TValue>", classChunk.Signature, StringComparison.Ordinal);
        Assert.Contains("where TKey : notnull", classChunk.Signature, StringComparison.Ordinal);

        Chunk methodChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Sample.Generics.Repository.Find");
        Assert.Contains("<TArg>", methodChunk.Signature, StringComparison.Ordinal);
        Assert.Contains("(TKey key, TArg arg)", methodChunk.Signature, StringComparison.Ordinal);
        Assert.Contains("where TArg : class", methodChunk.Signature, StringComparison.Ordinal);

        await AssertCommonInvariantsAsync("Generics.cs", chunks, ["System.Collections.Generic"]);
    }

    private static async Task<IReadOnlyList<Chunk>> ChunkFixtureAsync(string fixtureFileName, ChunkerOptions? options = null)
    {
        SkipIfTreeSitterUnavailable();

        string fixturePath = ResolveFixturePath(fixtureFileName);
        string source = await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken);

        ChunkerInput input = new(Path.Combine("Chunker", "Fixtures", "csharp", fixtureFileName), Language.CSharp, source);
        return await new CSharpChunker(options).ChunkAsync(input, TestContext.Current.CancellationToken);
    }

    private static async Task AssertCommonInvariantsAsync(string fixtureFileName, IReadOnlyList<Chunk> firstRun, IReadOnlyList<string> expectedImports, ChunkerOptions? options = null)
    {
        IReadOnlyList<Chunk> secondRun = await ChunkFixtureAsync(fixtureFileName, options);
        Assert.Equal(firstRun.Select(chunk => chunk.Id), secondRun.Select(chunk => chunk.Id));

        foreach (Chunk chunk in firstRun)
        {
            Assert.Equal(ChunkBuilder.CreateStableId(chunk.Path, chunk.FullyQualifiedName, chunk.Signature), chunk.Id);
            Assert.True(chunk.Range.StartLine <= chunk.Range.EndLine);
            Assert.True(chunk.Range.StartColumn <= chunk.Range.EndColumn || chunk.Range.StartLine < chunk.Range.EndLine);
            Assert.Equal(expectedImports, chunk.ImportsInScope.Select(import => import.Source).ToArray());
        }
    }

    private static string ResolveFixturePath(string fixtureFileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, "Chunker", "Fixtures", "csharp", fixtureFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        throw new FileNotFoundException("Unable to locate the C# chunker fixture.", fixtureFileName);
    }

    private static void SkipIfTreeSitterUnavailable()
    {
        if (!CanRun("node", "--version"))
        {
            Assert.Skip("node is not available on PATH.");
        }

        string sidecarPath = Path.Combine("E:\\Repos\\Agency", "tools", "treesitter-sidecar", "index.js");
        if (!File.Exists(sidecarPath))
        {
            Assert.Skip("Tree-sitter sidecar is not available yet.");
        }

        string nodeModulesPath = Path.Combine("E:\\Repos\\Agency", "tools", "treesitter-sidecar", "node_modules");
        if (!Directory.Exists(Path.Combine(nodeModulesPath, "tree-sitter-c-sharp")))
        {
            Assert.Skip("Tree-sitter sidecar dependencies are not installed yet.");
        }
    }

    private static bool CanRun(string fileName, string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start process.");

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
