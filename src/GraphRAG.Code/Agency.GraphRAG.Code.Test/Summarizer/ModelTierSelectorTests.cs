using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Tests for <see cref="ModelTierSelector"/>.
/// </summary>
public sealed class ModelTierSelectorTests
{
    private static readonly SummarizerOptions DefaultOptions = new()
    {
        StrongModel = "gpt-strong",
        StandardModel = "gpt-standard",
        CheapModel = "gpt-cheap",
        CheapestModel = "gpt-cheapest",
    };

    [Fact]
    public void SelectDetailedTier_Interface_UsesStrongTier()
    {
        ModelTierSelector selector = new(DefaultOptions);

        ModelTierSelector.ModelTier tier = selector.SelectDetailedTier(CreateChunk(SymbolKind.Interface, "public interface IWorker"), isLeaf: false);

        Assert.Equal(ModelTierSelector.ModelTier.Strong, tier);
        Assert.Equal("gpt-strong", selector.SelectDetailedModel(CreateChunk(SymbolKind.Interface, "public interface IWorker"), isLeaf: false));
    }

    [Fact]
    public void SelectDetailedTier_AbstractClass_UsesStrongTier()
    {
        ModelTierSelector selector = new(DefaultOptions);
        Chunk chunk = CreateChunk(SymbolKind.Class, "public abstract class WorkerBase", "public abstract class WorkerBase");

        ModelTierSelector.ModelTier tier = selector.SelectDetailedTier(chunk, isLeaf: false);

        Assert.Equal(ModelTierSelector.ModelTier.Strong, tier);
        Assert.Equal("gpt-strong", selector.SelectDetailedModel(chunk, isLeaf: false));
    }

    [Fact]
    public void SelectDetailedTier_NonLeafConcreteSymbol_UsesStandardTier()
    {
        ModelTierSelector selector = new(DefaultOptions);
        Chunk chunk = CreateChunk(SymbolKind.Class, "public sealed class StripeProcessor");

        ModelTierSelector.ModelTier tier = selector.SelectDetailedTier(chunk, isLeaf: false);

        Assert.Equal(ModelTierSelector.ModelTier.Standard, tier);
        Assert.Equal("gpt-standard", selector.SelectDetailedModel(chunk, isLeaf: false));
    }

    [Fact]
    public void SelectDetailedTier_LeafSymbol_UsesCheapTier()
    {
        ModelTierSelector selector = new(DefaultOptions);
        Chunk chunk = CreateChunk(SymbolKind.Method, "private void Normalize()");

        ModelTierSelector.ModelTier tier = selector.SelectDetailedTier(chunk, isLeaf: true);

        Assert.Equal(ModelTierSelector.ModelTier.Cheap, tier);
        Assert.Equal("gpt-cheap", selector.SelectDetailedModel(chunk, isLeaf: true));
    }

    [Fact]
    public void SelectOneLineTier_AlwaysUsesCheapestTier()
    {
        ModelTierSelector selector = new(DefaultOptions);

        Assert.Equal(ModelTierSelector.ModelTier.Cheapest, selector.SelectOneLineTier());
        Assert.Equal("gpt-cheapest", selector.SelectOneLineModel());
    }

    private static Chunk CreateChunk(SymbolKind symbolKind, string content, string? signature = null) =>
        new(
            "chunk-1",
            @"src\Payments\StripeProcessor.cs",
            Language.CSharp,
            ChunkGranularity.Type,
            "StripeProcessor",
            "Payments.StripeProcessor",
            signature,
            content,
            new ChunkSourceRange(1, 0, 1, 10),
            symbolKind,
            []);
}
