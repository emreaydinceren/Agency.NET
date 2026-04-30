using System.Diagnostics;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Chunker;

/// <summary>
/// Tests for <see cref="PythonChunker"/>.
/// </summary>
public sealed class PythonChunkerTests
{
    [Fact]
    public async Task Chunk_SimpleFixture_EmitsModuleClassMethodAndFunctionChunks()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("simple.py");

        Chunk moduleChunk = Assert.Single(chunks, chunk => chunk.Granularity == ChunkGranularity.Namespace);
        Assert.Equal(SymbolKind.Namespace, moduleChunk.SymbolKind);
        Assert.Equal("Chunker.Fixtures.python.simple", moduleChunk.FullyQualifiedName);

        Chunk classChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Chunker.Fixtures.python.simple.Worker");
        Assert.Equal(SymbolKind.Class, classChunk.SymbolKind);
        Assert.Equal(moduleChunk.Id, classChunk.ParentId);

        Chunk[] methods = chunks.Where(chunk => chunk.ParentId == classChunk.Id).OrderBy(chunk => chunk.Name).ToArray();
        Assert.Equal([SymbolKind.Method, SymbolKind.Method], methods.Select(chunk => chunk.SymbolKind).ToArray());
        Assert.Equal(["run", "stop"], methods.Select(chunk => chunk.Name).ToArray());
        Assert.Contains("value: int", methods[0].Signature, StringComparison.Ordinal);
        Assert.Contains("-> str", methods[0].Signature, StringComparison.Ordinal);

        Chunk[] functions = chunks.Where(chunk => chunk.SymbolKind == SymbolKind.Function).OrderBy(chunk => chunk.Name).ToArray();
        Assert.Equal(["build_name", "create_queue"], functions.Select(chunk => chunk.Name).ToArray());
        Assert.All(functions, function => Assert.Equal(moduleChunk.Id, function.ParentId));

        await AssertCommonInvariantsAsync("simple.py", chunks);
    }

    [Fact]
    public async Task Chunk_AbcFixture_DetectsInterfacesAndImplements()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("abc_interface.py");

        Chunk ifoo = Assert.Single(chunks, chunk => chunk.FullyQualifiedName.EndsWith(".IFoo", StringComparison.Ordinal));
        Assert.Equal(SymbolKind.Interface, ifoo.SymbolKind);
        Assert.Empty(ifoo.Implements ?? []);
        Assert.Empty(ifoo.Inherits ?? []);

        Chunk ibar = Assert.Single(chunks, chunk => chunk.FullyQualifiedName.EndsWith(".IBar", StringComparison.Ordinal));
        Assert.Equal(SymbolKind.Interface, ibar.SymbolKind);

        Chunk foo = Assert.Single(chunks, chunk => chunk.FullyQualifiedName.EndsWith(".Foo", StringComparison.Ordinal));
        Assert.Equal(SymbolKind.Class, foo.SymbolKind);
        Assert.Equal(["IFoo"], foo.Implements);
        Assert.Empty(foo.Inherits ?? []);

        Chunk runMethod = Assert.Single(chunks, chunk => chunk.FullyQualifiedName.EndsWith(".Foo.run", StringComparison.Ordinal));
        Assert.Contains("value: int", runMethod.Signature, StringComparison.Ordinal);

        await AssertCommonInvariantsAsync("abc_interface.py", chunks);
    }

    [Fact]
    public async Task Chunk_ProtocolFixture_DetectsTypingAndTypingExtensionsProtocolsAsInterfaces()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("protocol_interface.py");

        Chunk[] interfaces = chunks.Where(chunk => chunk.SymbolKind == SymbolKind.Interface).OrderBy(chunk => chunk.Name).ToArray();
        Assert.Equal(["IBar", "IFoo"], interfaces.Select(chunk => chunk.Name).ToArray());
        Assert.All(interfaces, chunk => Assert.Equal(ChunkGranularity.Type, chunk.Granularity));

        await AssertCommonInvariantsAsync("protocol_interface.py", chunks);
    }

    [Fact]
    public async Task Chunk_DuckTypedFixture_DoesNotEmitImplementsForStructuralConformance()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("duck_typed.py");

        Chunk foo = Assert.Single(chunks, chunk => chunk.FullyQualifiedName.EndsWith(".Foo", StringComparison.Ordinal));
        Assert.Empty(foo.Implements ?? []);

        await AssertCommonInvariantsAsync("duck_typed.py", chunks);
    }

    [Fact]
    public async Task Chunk_ImportsFixture_PreservesBareAliasedAndRelativeImports()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("imports.py");

        Chunk functionChunk = Assert.Single(chunks, chunk => chunk.SymbolKind == SymbolKind.Function && chunk.Granularity == ChunkGranularity.Member);
        ImportReference[] imports = functionChunk.ImportsInScope.OrderBy(import => import.Source).ThenBy(import => import.Alias).ToArray();

        Assert.Collection(
            imports,
            import =>
            {
                Assert.Equal(".helpers", import.Source);
                Assert.Equal(["bar"], import.Symbols);
                Assert.True(import.IsRelative);
            },
            import =>
            {
                Assert.Equal("collections", import.Source);
                Assert.Equal(["deque"], import.Symbols);
                Assert.False(import.IsRelative);
            },
            import =>
            {
                Assert.Equal("numpy", import.Source);
                Assert.Empty(import.Symbols);
                Assert.Equal("np", import.Alias);
                Assert.False(import.IsRelative);
            },
            import =>
            {
                Assert.Equal("os", import.Source);
                Assert.Empty(import.Symbols);
                Assert.False(import.IsRelative);
            });

        await AssertCommonInvariantsAsync("imports.py", chunks);
    }

    [Fact]
    public async Task Chunk_LargeFunctionFixture_EmitsStatementFallbackChunks()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("large_function.py", new ChunkerOptions { MaxChunkChars = 40 });

        Chunk functionChunk = Assert.Single(chunks, chunk => chunk.SymbolKind == SymbolKind.Function && chunk.Granularity == ChunkGranularity.Member);
        Chunk[] statements = chunks.Where(chunk => chunk.Granularity == ChunkGranularity.Statement).OrderBy(chunk => chunk.Name).ToArray();

        Assert.True(statements.Length >= 4);
        Assert.All(statements, statement => Assert.Equal(functionChunk.Id, statement.ParentId));
        Assert.Contains(statements, chunk => chunk.Content.Contains("for value in values", StringComparison.Ordinal));
        Assert.Contains(statements, chunk => chunk.Content.Contains("return total", StringComparison.Ordinal));

        await AssertCommonInvariantsAsync("large_function.py", chunks, new ChunkerOptions { MaxChunkChars = 40 });
    }

    private static async Task<IReadOnlyList<Chunk>> ChunkFixtureAsync(string fixtureFileName, ChunkerOptions? options = null)
    {
        SkipIfTreeSitterUnavailable();

        string fixturePath = ResolveFixturePath(fixtureFileName);
        string source = await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken);

        ChunkerInput input = new(Path.Combine("Chunker", "Fixtures", "python", fixtureFileName), Language.Python, source);
        return await new PythonChunker(options).ChunkAsync(input, TestContext.Current.CancellationToken);
    }

    private static async Task AssertCommonInvariantsAsync(string fixtureFileName, IReadOnlyList<Chunk> firstRun, ChunkerOptions? options = null)
    {
        IReadOnlyList<Chunk> secondRun = await ChunkFixtureAsync(fixtureFileName, options);
        Assert.Equal(firstRun.Select(chunk => chunk.Id), secondRun.Select(chunk => chunk.Id));

        foreach (Chunk chunk in firstRun)
        {
            Assert.Equal(ChunkBuilder.CreateStableId(chunk.Path, chunk.FullyQualifiedName, chunk.Signature), chunk.Id);
            Assert.True(chunk.Range.StartLine <= chunk.Range.EndLine);
            Assert.True(chunk.Range.StartColumn <= chunk.Range.EndColumn || chunk.Range.StartLine < chunk.Range.EndLine);
        }
    }

    private static string ResolveFixturePath(string fixtureFileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, "Chunker", "Fixtures", "python", fixtureFileName);
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

        throw new FileNotFoundException("Unable to locate the Python chunker fixture.", fixtureFileName);
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

        string[] requiredPackages =
        [
            "tree-sitter",
            "tree-sitter-python",
        ];

        string nodeModulesPath = Path.Combine("E:\\Repos\\Agency", "tools", "treesitter-sidecar", "node_modules");
        if (requiredPackages.Any(package => !Directory.Exists(Path.Combine(nodeModulesPath, package))))
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
