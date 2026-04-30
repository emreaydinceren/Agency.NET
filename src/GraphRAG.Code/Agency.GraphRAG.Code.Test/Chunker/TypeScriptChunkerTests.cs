using System.Diagnostics;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Chunker;

/// <summary>
/// Tests for <see cref="TypeScriptChunker"/>.
/// </summary>
public sealed class TypeScriptChunkerTests
{
    [Fact]
    public async Task Chunk_SimpleFixture_EmitsModuleClassFunctionsAndStatementFallback()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Simple.ts", Language.TypeScript, new ChunkerOptions { MaxChunkChars = 80 });

        Chunk moduleChunk = Assert.Single(chunks, chunk => chunk.Granularity == ChunkGranularity.Namespace);
        Assert.Equal("Simple", moduleChunk.FullyQualifiedName);

        Chunk classChunk = Assert.Single(chunks, chunk => chunk.SymbolKind == SymbolKind.Class && chunk.Name == "Worker");
        Assert.Equal(moduleChunk.Id, classChunk.ParentId);

        Chunk[] functions = chunks
            .Where(chunk => chunk.SymbolKind == SymbolKind.Function && chunk.Granularity == ChunkGranularity.Member)
            .OrderBy(chunk => chunk.Name)
            .ToArray();
        Assert.Equal(["buildMessage", "formatUser"], functions.Select(chunk => chunk.Name).ToArray());
        Assert.All(functions, chunk => Assert.Equal(moduleChunk.Id, chunk.ParentId));
        Assert.Contains("(name: string): string", functions.Single(chunk => chunk.Name == "formatUser").Signature, StringComparison.Ordinal);

        Chunk methodChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Worker.run");
        Assert.Contains("(task: string): Promise<string>", methodChunk.Signature, StringComparison.Ordinal);

        Chunk[] statementChunks = chunks.Where(chunk => chunk.Granularity == ChunkGranularity.Statement).OrderBy(chunk => chunk.Name).ToArray();
        Assert.NotEmpty(statementChunks);
        Assert.Contains(statementChunks, chunk => chunk.Content.Contains("const parts =", StringComparison.Ordinal));
        Assert.Contains(statementChunks, chunk => chunk.Content.Contains("return message.trim();", StringComparison.Ordinal));

        await AssertStableIdsAsync("Simple.ts", Language.TypeScript, chunks, new ChunkerOptions { MaxChunkChars = 80 });
    }

    [Fact]
    public async Task Chunk_InterfaceFixture_CapturesInheritsAndImplements()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Interface.ts", Language.TypeScript);

        Chunk interfaceChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Worker");
        Assert.Equal(SymbolKind.Interface, interfaceChunk.SymbolKind);
        Assert.Equal(["Disposable", "Runner<TaskContext>"], interfaceChunk.Inherits);

        Chunk abstractClassChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "WorkerBase");
        Assert.Equal(SymbolKind.Class, abstractClassChunk.SymbolKind);
        Assert.Empty(abstractClassChunk.Inherits ?? []);
        Assert.Equal(["Worker"], abstractClassChunk.Implements);

        Chunk concreteChunk = Assert.Single(chunks, chunk => chunk.FullyQualifiedName == "Service");
        Assert.Equal(["WorkerBase"], concreteChunk.Inherits);
        Assert.Equal(["Worker", "Disposable"], concreteChunk.Implements);

        await AssertStableIdsAsync("Interface.ts", Language.TypeScript, chunks);
    }

    [Fact]
    public async Task Chunk_ImportsFixture_CapturesSourcesSymbolsAliasesAndRelativeFlags()
    {
        IReadOnlyList<Chunk> chunks = await ChunkFixtureAsync("Imports.ts", Language.TypeScript);

        IReadOnlyList<ImportReference> imports = Assert.Single(chunks, chunk => chunk.Name == "Imports" && chunk.Granularity == ChunkGranularity.Namespace).ImportsInScope;
        Assert.Contains(imports, import => import.Source == "react" && import.Symbols.SequenceEqual(["default"]) && import.Alias == "React" && !import.IsRelative);
        Assert.Contains(imports, import => import.Source == "react" && import.Symbols.SequenceEqual(["useMemo"]) && import.Alias == "memo" && !import.IsRelative);
        Assert.Contains(imports, import => import.Source == "react" && import.Symbols.SequenceEqual(["FC"]) && import.Alias is null && !import.IsRelative);
        Assert.Contains(imports, import => import.Source == "./utils" && import.Symbols.SequenceEqual(["*"]) && import.Alias == "utils" && import.IsRelative);
        Assert.Contains(imports, import => import.Source == "zone.js" && import.Symbols.Count == 0 && !import.IsRelative);

        Assert.All(chunks, chunk => Assert.Equal(imports, chunk.ImportsInScope));
    }

    [Fact]
    public async Task Chunk_TsxAndJavaScriptFixtures_ParseJsxAndRequire()
    {
        IReadOnlyList<Chunk> tsxChunks = await ChunkFixtureAsync("JsxComponent.tsx", Language.Tsx);
        Assert.Contains(tsxChunks, chunk => chunk.SymbolKind == SymbolKind.Function && chunk.Name == "Component");
        Assert.Contains(tsxChunks, chunk => chunk.SymbolKind == SymbolKind.Class && chunk.Name == "ClassComponent");
        Assert.Contains(tsxChunks, chunk => chunk.FullyQualifiedName == "ClassComponent.render" && chunk.Signature!.Contains("(): JSX.Element", StringComparison.Ordinal));

        IReadOnlyList<Chunk> jsChunks = await ChunkFixtureAsync("JsModule.js", Language.JavaScript);
        Assert.Contains(jsChunks, chunk => chunk.SymbolKind == SymbolKind.Function && chunk.Name == "build");
        Assert.Contains(jsChunks, chunk => chunk.SymbolKind == SymbolKind.Function && chunk.Name == "format");

        IReadOnlyList<ImportReference> jsImports = Assert.Single(jsChunks, chunk => chunk.Name == "JsModule" && chunk.Granularity == ChunkGranularity.Namespace).ImportsInScope;
        Assert.Contains(jsImports, import => import.Source == "node:path" && import.Alias == "path" && import.Symbols.SequenceEqual(["default"]));

        await AssertStableIdsAsync("JsxComponent.tsx", Language.Tsx, tsxChunks);
        await AssertStableIdsAsync("JsModule.js", Language.JavaScript, jsChunks);
    }

    private static async Task<IReadOnlyList<Chunk>> ChunkFixtureAsync(string fixtureFileName, Language language, ChunkerOptions? options = null)
    {
        SkipIfTreeSitterUnavailable();

        string fixturePath = ResolveFixturePath(fixtureFileName);
        string source = await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken);

        ChunkerInput input = new(Path.Combine("Chunker", "Fixtures", "typescript", fixtureFileName), language, source);
        return await new TypeScriptChunker(options).ChunkAsync(input, TestContext.Current.CancellationToken);
    }

    private static async Task AssertStableIdsAsync(string fixtureFileName, Language language, IReadOnlyList<Chunk> firstRun, ChunkerOptions? options = null)
    {
        IReadOnlyList<Chunk> secondRun = await ChunkFixtureAsync(fixtureFileName, language, options);
        Assert.Equal(firstRun.Select(chunk => chunk.Id), secondRun.Select(chunk => chunk.Id));

        foreach (Chunk chunk in firstRun)
        {
            Assert.Equal(ChunkBuilder.CreateStableId(chunk.Path, chunk.FullyQualifiedName, chunk.Signature), chunk.Id);
        }
    }

    private static string ResolveFixturePath(string fixtureFileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, "Chunker", "Fixtures", "typescript", fixtureFileName);
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

        throw new FileNotFoundException("Unable to locate the TypeScript chunker fixture.", fixtureFileName);
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
        if (!Directory.Exists(Path.Combine(nodeModulesPath, "tree-sitter-typescript")) ||
            !Directory.Exists(Path.Combine(nodeModulesPath, "tree-sitter-javascript")))
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
