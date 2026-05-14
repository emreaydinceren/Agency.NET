using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.ChangeDetector;

/// <summary>
/// Tests for <see cref="Agency.GraphRAG.Code.ChangeDetector.ChangeDetector"/>.
/// </summary>
public sealed class ChangeDetectorTests
{
    [Fact]
    public void Detect_ProducesExpectedChangeBuckets()
    {
        Agency.GraphRAG.Code.ChangeDetector.ChangeDetector detector = new();
        Guid preservedSymbolId = Guid.NewGuid();
        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            HeadCommit = "head",
            IsShallowRepository = false,
            Files =
            [
                new WalkedFile { Path = @"src\Added.cs", Status = WalkedFileStatus.Added, Language = Language.CSharp },
                new WalkedFile { Path = @"src\OrderService.cs", Status = WalkedFileStatus.Modified, Language = Language.CSharp },
                new WalkedFile { Path = @"src\Removed.cs", Status = WalkedFileStatus.Deleted, Language = Language.CSharp },
                new WalkedFile { Path = @"src\RenamedNew.cs", OldPath = @"src\RenamedOld.cs", Status = WalkedFileStatus.Renamed, Language = Language.CSharp },
                new WalkedFile { Path = @"src\Billing\Billing.csproj", Status = WalkedFileStatus.Modified, Language = Language.Unknown },
            ],
        };
        Dictionary<string, IReadOnlyList<Symbol>> storedSymbols = new(StringComparer.Ordinal)
        {
            [@"src\OrderService.cs"] =
            [
                CreateSymbol(Guid.NewGuid(), "Orders.OrderService.Submit", "old-submit"),
                CreateSymbol(Guid.NewGuid(), "Orders.OrderService.Cancel", "cancel"),
            ],
            [@"src\RenamedOld.cs"] =
            [
                CreateSymbol(preservedSymbolId, "Orders.Renamed.Type", "rename"),
            ],
        };
        Dictionary<string, IReadOnlyList<Chunk>> currentChunks = new(StringComparer.Ordinal)
        {
            [@"src\OrderService.cs"] =
            [
                CreateChunk(@"src\OrderService.cs", "Orders.OrderService.Submit", "new-submit"),
                CreateChunk(@"src\OrderService.cs", "Orders.OrderService.Add", "add"),
            ],
        };

        ChangeSet changeSet = detector.Detect(walkResult, storedSymbols, currentChunks);

        Assert.Equal([@"src\Added.cs"], changeSet.AddedFiles);
        Assert.Equal([@"src\Removed.cs"], changeSet.DeletedFiles);
        Assert.Equal([@"src\Billing\Billing.csproj"], changeSet.ManifestChanges);

        RenamedFileChange rename = Assert.Single(changeSet.RenamedFiles);
        Assert.Equal(@"src\RenamedOld.cs", rename.OldPath);
        Assert.Equal(@"src\RenamedNew.cs", rename.NewPath);
        Assert.Equal([preservedSymbolId], rename.PreservedSymbolIds);

        ModifiedFileChange modified = Assert.Single(changeSet.ModifiedFiles);
        Assert.Equal(@"src\OrderService.cs", modified.Path);
        Assert.Equal(3, modified.Changes.Count);
        Assert.Contains(modified.Changes, static change => change.Kind == SymbolChangeKind.Modified && change.FullyQualifiedName == "Orders.OrderService.Submit");
        Assert.Contains(modified.Changes, static change => change.Kind == SymbolChangeKind.Added && change.FullyQualifiedName == "Orders.OrderService.Add");
        Assert.Contains(modified.Changes, static change => change.Kind == SymbolChangeKind.Deleted && change.FullyQualifiedName == "Orders.OrderService.Cancel");
    }

    [Fact]
    public void Detect_TracksManifestRenameUsingOldAndNewPaths()
    {
        Agency.GraphRAG.Code.ChangeDetector.ChangeDetector detector = new();
        WalkResult walkResult = new()
        {
            Mode = WalkMode.Incremental,
            HeadCommit = "head",
            IsShallowRepository = false,
            Files =
            [
                new WalkedFile
                {
                    Path = Path.Combine("src", "package.json"),
                    OldPath = Path.Combine("src", "package-old.json"),
                    Status = WalkedFileStatus.Renamed,
                    Language = Language.JavaScript,
                },
            ],
        };

        ChangeSet changeSet = detector.Detect(
            walkResult,
            new Dictionary<string, IReadOnlyList<Symbol>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<Chunk>>(StringComparer.Ordinal));

        Assert.Equal([Path.Combine("src", "package.json")], changeSet.ManifestChanges);
    }

    private static Symbol CreateSymbol(Guid id, string fullyQualifiedName, string content) =>
        new()
        {
            Id = id,
            FileId = Guid.NewGuid(),
            ModuleId = null,
            Name = fullyQualifiedName.Split('.').Last(),
            FullyQualifiedName = fullyQualifiedName,
            Kind = SymbolKind.Method,
            Signature = null,
            Summary = null,
            OneLineSummary = null,
            ContentHash = ComputeContentHash(content),
            Embedding = null,
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 2,
        };

    private static Chunk CreateChunk(string path, string fullyQualifiedName, string content) =>
        ChunkBuilder.Build(
            path: path,
            language: Language.CSharp,
            granularity: ChunkGranularity.Member,
            name: fullyQualifiedName.Split('.').Last(),
            fullyQualifiedName: fullyQualifiedName,
            signature: null,
            content: content,
            range: new ChunkSourceRange(1, 0, 2, 0),
            symbolKind: SymbolKind.Method,
            importsInScope: []);

    private static string ComputeContentHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
