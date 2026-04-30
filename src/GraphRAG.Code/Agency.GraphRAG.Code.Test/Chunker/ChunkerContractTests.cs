using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Chunker;

/// <summary>
/// Defines the language-agnostic contract that chunker implementations must satisfy.
/// </summary>
public abstract class ChunkerContractTests
{
    /// <summary>
    /// Creates the chunker under test.
    /// </summary>
    /// <returns>A chunker implementation.</returns>
    protected abstract IChunker CreateChunker();

    [Fact]
    public void Chunker_ExposesExpectedAsyncContract()
    {
        IChunker chunker = this.CreateChunker();

        Assert.IsAssignableFrom<IChunker>(chunker);

        System.Reflection.MethodInfo? method = typeof(IChunker).GetMethod(nameof(IChunker.ChunkAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IReadOnlyList<Chunk>>), method.ReturnType);

        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ChunkerInput), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public async Task Chunker_EmitsNamespaceTypeMemberAndStatementChunksWithRangeAndImports()
    {
        IChunker chunker = this.CreateChunker();
        ChunkerInput input = new("src/Services/Worker.cs", Language.CSharp, "namespace Example.Services { class Worker { void Run() { Execute(); } } }");

        IReadOnlyList<Chunk> chunks = await chunker.ChunkAsync(input, TestContext.Current.CancellationToken);

        Assert.Collection(
            chunks.OrderBy(chunk => chunk.Range.StartLine).ThenBy(chunk => chunk.Range.StartColumn),
            namespaceChunk =>
            {
                Assert.Equal(ChunkGranularity.Namespace, namespaceChunk.Granularity);
                Assert.Equal(SymbolKind.Namespace, namespaceChunk.SymbolKind);
                Assert.Equal("Example.Services", namespaceChunk.FullyQualifiedName);
                Assert.Equal(@"src\Services\Worker.cs", namespaceChunk.Path);
                Assert.Single(namespaceChunk.ImportsInScope);
                Assert.Equal(new ChunkSourceRange(0, 0, 8, 1), namespaceChunk.Range);
            },
            typeChunk =>
            {
                Assert.Equal(ChunkGranularity.Type, typeChunk.Granularity);
                Assert.Equal(SymbolKind.Class, typeChunk.SymbolKind);
                Assert.Equal("Example.Services.Worker", typeChunk.FullyQualifiedName);
                Assert.Equal(new ChunkSourceRange(1, 0, 7, 1), typeChunk.Range);
                Assert.NotNull(typeChunk.ParentId);
            },
            memberChunk =>
            {
                Assert.Equal(ChunkGranularity.Member, memberChunk.Granularity);
                Assert.Equal(SymbolKind.Method, memberChunk.SymbolKind);
                Assert.Equal("void Run()", memberChunk.Signature);
                Assert.Equal("Example.Services.Worker.Run", memberChunk.FullyQualifiedName);
                Assert.Equal(new ChunkSourceRange(3, 4, 6, 5), memberChunk.Range);
                Assert.NotNull(memberChunk.ParentId);
            },
            statementChunk =>
            {
                Assert.Equal(ChunkGranularity.Statement, statementChunk.Granularity);
                Assert.Equal(SymbolKind.Method, statementChunk.SymbolKind);
                Assert.Equal("statement#1", statementChunk.Name);
                Assert.Equal("statement-1", statementChunk.Signature);
                Assert.Equal("Example.Services.Worker.Run#statement:1", statementChunk.FullyQualifiedName);
                Assert.Equal(new ChunkSourceRange(4, 8, 4, 18), statementChunk.Range);
                Assert.NotNull(statementChunk.ParentId);
            });
    }

    [Fact]
    public async Task Chunker_ProducesStableChunkIdsAcrossRuns()
    {
        IChunker chunker = this.CreateChunker();
        ChunkerInput input = new(@"src\Services\Worker.cs", Language.CSharp, "class Worker {}");

        IReadOnlyList<Chunk> first = await chunker.ChunkAsync(input, TestContext.Current.CancellationToken);
        IReadOnlyList<Chunk> second = await chunker.ChunkAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(first.Select(chunk => chunk.Id), second.Select(chunk => chunk.Id));
    }

    [Fact]
    public void ChunkBuilder_UsesPathSymbolAndSignatureForStableIds()
    {
        string id = ChunkBuilder.CreateStableId("src/Services/Worker.cs", "Example.Services.Worker.Run", "void Run()");
        string expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("src\\Services\\Worker.cs\nExample.Services.Worker.Run\nvoid Run()")));

        Assert.Equal(expected, id);
    }
}

/// <summary>
/// Executes the language-agnostic chunker contract against a reference implementation backed by <see cref="ChunkBuilder"/>.
/// </summary>
public sealed class ReferenceChunkerContractTests : ChunkerContractTests
{
    /// <inheritdoc />
    protected override IChunker CreateChunker() => new ReferenceChunker();

    private sealed class ReferenceChunker : IChunker
    {
        public Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);

            ImportReference[] imports =
            [
                new("System.Collections.Generic", ["List"], IsRelative: false),
            ];

            Chunk namespaceChunk = ChunkBuilder.Build(
                input.Path,
                input.Language,
                ChunkGranularity.Namespace,
                "Example.Services",
                "Example.Services",
                signature: null,
                content: "namespace Example.Services",
                range: new ChunkSourceRange(0, 0, 8, 1),
                symbolKind: SymbolKind.Namespace,
                importsInScope: imports);

            Chunk typeChunk = ChunkBuilder.Build(
                input.Path,
                input.Language,
                ChunkGranularity.Type,
                "Worker",
                "Example.Services.Worker",
                signature: "class Worker",
                content: "class Worker",
                range: new ChunkSourceRange(1, 0, 7, 1),
                symbolKind: SymbolKind.Class,
                importsInScope: imports,
                parentId: namespaceChunk.Id);

            Chunk memberChunk = ChunkBuilder.Build(
                input.Path,
                input.Language,
                ChunkGranularity.Member,
                "Run",
                "Example.Services.Worker.Run",
                signature: "void Run()",
                content: "void Run() { Execute(); }",
                range: new ChunkSourceRange(3, 4, 6, 5),
                symbolKind: SymbolKind.Method,
                importsInScope: imports,
                parentId: typeChunk.Id);

            Chunk statementChunk = ChunkBuilder.Build(
                input.Path,
                input.Language,
                ChunkGranularity.Statement,
                "statement#1",
                ChunkBuilder.CreateStatementSymbolName(memberChunk.FullyQualifiedName, 1),
                signature: "statement-1",
                content: "Execute();",
                range: new ChunkSourceRange(4, 8, 4, 18),
                symbolKind: SymbolKind.Method,
                importsInScope: imports,
                parentId: memberChunk.Id);

            IReadOnlyList<Chunk> chunks = [namespaceChunk, typeChunk, memberChunk, statementChunk];
            return Task.FromResult(chunks);
        }
    }
}
