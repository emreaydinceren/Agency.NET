using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.TreeSitter.Pipeline;
using Agency.GraphRAG.Code.Walker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.GraphRAG.Code.Test.TreeSitter.Pipeline;

/// <summary>
/// Tests for <see cref="WriteRequestBuilder"/>.
/// </summary>
public sealed class WriteRequestBuilderTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;

    public ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BuildAsync_SkipDeletedFiles()
    {
        RecordingChunkerDispatcher recordingChunker = new();
        ChunkerDispatcher chunkerDispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = recordingChunker,
            [Language.TypeScript] = recordingChunker,
            [Language.Tsx] = recordingChunker,
            [Language.JavaScript] = recordingChunker,
            [Language.Jsx] = recordingChunker,
            [Language.Python] = recordingChunker,
        });
        WriteRequestBuilder builder = new(chunkerDispatcher, NullLogger<WriteRequestBuilder>.Instance);

        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = _tempDir,
            IsShallow = false,
        };

        WalkedFile deletedFile = new()
        {
            Path = "deleted.cs",
            Status = WalkedFileStatus.Deleted,
            Language = Language.CSharp,
        };

        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            Files = [deletedFile],
            HeadCommit = "abc123",
            IsShallowRepository = false,
        };

        IReadOnlyDictionary<string, Phase1WriteRequest> result = await builder.BuildAsync(repo, walkResult, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildAsync_SkipsUnknownLanguageFiles()
    {
        RecordingChunkerDispatcher recordingChunker = new();
        ChunkerDispatcher chunkerDispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = recordingChunker,
            [Language.TypeScript] = recordingChunker,
            [Language.Tsx] = recordingChunker,
            [Language.JavaScript] = recordingChunker,
            [Language.Jsx] = recordingChunker,
            [Language.Python] = recordingChunker,
        });
        WriteRequestBuilder builder = new(chunkerDispatcher, NullLogger<WriteRequestBuilder>.Instance);

        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = _tempDir,
            IsShallow = false,
        };

        WalkedFile unknownFile = new()
        {
            Path = "data.json",
            Status = WalkedFileStatus.Added,
            Language = Language.Unknown,
        };

        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            Files = [unknownFile],
            HeadCommit = "abc123",
            IsShallowRepository = false,
        };

        IReadOnlyDictionary<string, Phase1WriteRequest> result = await builder.BuildAsync(repo, walkResult, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildAsync_ProcessesAddedCSharpFile()
    {
        string sourceCode = "public class Foo { public void Bar() { } }";
        string filePath = Path.Combine(_tempDir, "src", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, sourceCode, TestContext.Current.CancellationToken);

        RecordingChunkerDispatcher recordingChunker = new();
        ChunkerDispatcher chunkerDispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = recordingChunker,
            [Language.TypeScript] = recordingChunker,
            [Language.Tsx] = recordingChunker,
            [Language.JavaScript] = recordingChunker,
            [Language.Jsx] = recordingChunker,
            [Language.Python] = recordingChunker,
        });
        WriteRequestBuilder builder = new(chunkerDispatcher, NullLogger<WriteRequestBuilder>.Instance);

        Guid repoId = Guid.NewGuid();
        Repo repo = new()
        {
            Id = repoId,
            LocalPath = _tempDir,
            IsShallow = false,
        };

        WalkedFile file = new()
        {
            Path = "src/Foo.cs",
            Status = WalkedFileStatus.Added,
            Language = Language.CSharp,
        };

        WalkResult walkResult = new()
        {
            Mode = WalkMode.Full,
            Files = [file],
            HeadCommit = "abc123",
            IsShallowRepository = false,
        };

        IReadOnlyDictionary<string, Phase1WriteRequest> result = await builder.BuildAsync(repo, walkResult, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Phase1WriteRequest request = result.Values.First();
        Assert.NotNull(request.File);
        Assert.Equal("src/Foo.cs", request.File.Path);
        Assert.Equal(repoId, request.File.RepoId);
        Assert.Equal("csharp", request.File.Language);
        Assert.NotNull(request.File.ContentHash);
        Assert.NotNull(request.Chunks);
        Assert.Empty(request.Summaries);
        Assert.Empty(request.UnresolvedCallSites);
    }

    [Fact]
    public async Task BuildAsync_ProcessesModifiedTypeScriptFile()
    {
        string sourceCode = "export function foo(): void {}";
        string filePath = Path.Combine(_tempDir, "src", "index.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, sourceCode, TestContext.Current.CancellationToken);

        RecordingChunkerDispatcher recordingChunker = new();
        ChunkerDispatcher chunkerDispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = recordingChunker,
            [Language.TypeScript] = recordingChunker,
            [Language.Tsx] = recordingChunker,
            [Language.JavaScript] = recordingChunker,
            [Language.Jsx] = recordingChunker,
            [Language.Python] = recordingChunker,
        });
        WriteRequestBuilder builder = new(chunkerDispatcher, NullLogger<WriteRequestBuilder>.Instance);

        Guid repoId = Guid.NewGuid();
        Repo repo = new()
        {
            Id = repoId,
            LocalPath = _tempDir,
            IsShallow = false,
        };

        WalkedFile file = new()
        {
            Path = "src/index.ts",
            Status = WalkedFileStatus.Modified,
            Language = Language.TypeScript,
        };

        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            Files = [file],
            HeadCommit = "def456",
            IsShallowRepository = false,
        };

        IReadOnlyDictionary<string, Phase1WriteRequest> result = await builder.BuildAsync(repo, walkResult, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Phase1WriteRequest request = result.Values.First();
        Assert.Equal("typescript", request.File.Language);
    }

    [Fact]
    public async Task BuildAsync_ComputesStableFileIdFromPath()
    {
        string sourceCode = "class Bar {}";
        string filePath = Path.Combine(_tempDir, "src", "Bar.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, sourceCode, TestContext.Current.CancellationToken);

        RecordingChunkerDispatcher recordingChunker = new();
        ChunkerDispatcher chunkerDispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = recordingChunker,
            [Language.TypeScript] = recordingChunker,
            [Language.Tsx] = recordingChunker,
            [Language.JavaScript] = recordingChunker,
            [Language.Jsx] = recordingChunker,
            [Language.Python] = recordingChunker,
        });
        WriteRequestBuilder builder = new(chunkerDispatcher, NullLogger<WriteRequestBuilder>.Instance);

        Guid repoId = Guid.NewGuid();
        Repo repo = new()
        {
            Id = repoId,
            LocalPath = _tempDir,
            IsShallow = false,
        };

        WalkedFile file = new()
        {
            Path = "src/Bar.cs",
            Status = WalkedFileStatus.Added,
            Language = Language.CSharp,
        };

        WalkResult walkResult = new()
        {
            Mode = WalkMode.Full,
            Files = [file],
            HeadCommit = "xyz",
            IsShallowRepository = false,
        };

        IReadOnlyDictionary<string, Phase1WriteRequest> result = await builder.BuildAsync(repo, walkResult, TestContext.Current.CancellationToken);

        Phase1WriteRequest request = result.Values.First();
        Guid expectedId = HydrationIds.StableGuid($"file:{repoId}:src/Bar.cs");
        Assert.Equal(expectedId, request.File.Id);
    }

    [Fact]
    public async Task BuildAsync_ProcessesMultipleFiles()
    {
        string filePath1 = Path.Combine(_tempDir, "src", "File1.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath1)!);
        await File.WriteAllTextAsync(filePath1, "class File1 {}", TestContext.Current.CancellationToken);

        string filePath2 = Path.Combine(_tempDir, "src", "File2.ts");
        await File.WriteAllTextAsync(filePath2, "export class File2 {}", TestContext.Current.CancellationToken);

        RecordingChunkerDispatcher recordingChunker = new();
        ChunkerDispatcher chunkerDispatcher = new(new Dictionary<Language, IChunker>
        {
            [Language.CSharp] = recordingChunker,
            [Language.TypeScript] = recordingChunker,
            [Language.Tsx] = recordingChunker,
            [Language.JavaScript] = recordingChunker,
            [Language.Jsx] = recordingChunker,
            [Language.Python] = recordingChunker,
        });
        WriteRequestBuilder builder = new(chunkerDispatcher, NullLogger<WriteRequestBuilder>.Instance);

        Guid repoId = Guid.NewGuid();
        Repo repo = new()
        {
            Id = repoId,
            LocalPath = _tempDir,
            IsShallow = false,
        };

        WalkedFile file1 = new()
        {
            Path = "src/File1.cs",
            Status = WalkedFileStatus.Added,
            Language = Language.CSharp,
        };

        WalkedFile file2 = new()
        {
            Path = "src/File2.ts",
            Status = WalkedFileStatus.Modified,
            Language = Language.TypeScript,
        };

        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            Files = [file1, file2],
            HeadCommit = "multi",
            IsShallowRepository = false,
        };

        IReadOnlyDictionary<string, Phase1WriteRequest> result = await builder.BuildAsync(repo, walkResult, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    private sealed class RecordingChunkerDispatcher : IChunker
    {
        public List<ChunkerInput> Inputs { get; } = [];

        public Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
        {
            this.Inputs.Add(input);

            IReadOnlyList<Chunk> chunks = [];
            return Task.FromResult(chunks);
        }
    }
}
