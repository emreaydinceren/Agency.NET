using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Chunker;

/// <summary>
/// Tests for <see cref="ChunkerDispatcher"/>.
/// </summary>
public sealed class ChunkerDispatcherTests
{
    [Theory]
    [InlineData(Language.CSharp, "csharp")]
    [InlineData(Language.TypeScript, "typescript")]
    [InlineData(Language.Tsx, "typescript")]
    [InlineData(Language.JavaScript, "typescript")]
    [InlineData(Language.Jsx, "typescript")]
    [InlineData(Language.Python, "python")]
    public async Task ChunkAsync_RoutesToRegisteredChunker(Language language, string expectedChunkerName)
    {
        RecordingChunker csharpChunker = new("csharp");
        RecordingChunker typeScriptChunker = new("typescript");
        RecordingChunker pythonChunker = new("python");
        ChunkerDispatcher dispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = csharpChunker,
            [Language.TypeScript] = typeScriptChunker,
            [Language.Tsx] = typeScriptChunker,
            [Language.JavaScript] = typeScriptChunker,
            [Language.Jsx] = typeScriptChunker,
            [Language.Python] = pythonChunker,
        });

        ChunkerInput input = new($"src\\sample.{language}", language, "content");

        IReadOnlyList<Chunk> chunks = await dispatcher.ChunkAsync(input, TestContext.Current.CancellationToken);

        Chunk chunk = Assert.Single(chunks);
        Assert.Equal(expectedChunkerName, chunk.Name);
        Assert.Equal(input, GetChunker(expectedChunkerName, csharpChunker, typeScriptChunker, pythonChunker).Inputs.Single());
        Assert.All(
            GetAllChunkers(csharpChunker, typeScriptChunker, pythonChunker).Where(chunker => !ReferenceEquals(chunker, GetChunker(expectedChunkerName, csharpChunker, typeScriptChunker, pythonChunker))),
            chunker => Assert.Empty(chunker.Inputs));
    }

    [Fact]
    public async Task ChunkAsync_ThrowsWhenLanguageIsUnsupported()
    {
        ChunkerDispatcher dispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = new RecordingChunker("csharp"),
        });

        ChunkerInput input = new("src\\sample.txt", Language.Unknown, "content");

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => dispatcher.ChunkAsync(input, TestContext.Current.CancellationToken));

        Assert.Equal("No chunker is registered for language 'Unknown'.", exception.Message);
    }

    private static RecordingChunker GetChunker(
        string name,
        RecordingChunker csharpChunker,
        RecordingChunker typeScriptChunker,
        RecordingChunker pythonChunker)
        => name switch
        {
            "csharp" => csharpChunker,
            "typescript" => typeScriptChunker,
            "python" => pythonChunker,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unsupported test chunker."),
        };

    private static IEnumerable<RecordingChunker> GetAllChunkers(
        RecordingChunker csharpChunker,
        RecordingChunker typeScriptChunker,
        RecordingChunker pythonChunker)
    {
        yield return csharpChunker;
        yield return typeScriptChunker;
        yield return pythonChunker;
    }

    private sealed class RecordingChunker(string name) : IChunker
    {
        public List<ChunkerInput> Inputs { get; } = [];

        public Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
        {
            this.Inputs.Add(input);

            IReadOnlyList<Chunk> chunks =
            [
                ChunkBuilder.Build(
                    input.Path,
                    input.Language,
                    ChunkGranularity.Member,
                    name,
                    $"Example.{name}",
                    "signature",
                    input.Source,
                    new ChunkSourceRange(0, 0, 0, Math.Max(0, input.Source.Length - 1)),
                    SymbolKind.Method,
                    []),
            ];

            return Task.FromResult(chunks);
        }
    }
}
