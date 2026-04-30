using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Domain;

/// <summary>
/// Tests for <see cref="Symbol"/> and <see cref="SymbolKind"/>.
/// </summary>
public sealed class SymbolTests
{
    [Fact]
    public void Symbol_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };

        var symbol = new Symbol
        {
            Id = id,
            FileId = fileId,
            ModuleId = moduleId,
            Name = "MyMethod",
            FullyQualifiedName = "MyNamespace.MyClass.MyMethod",
            Kind = SymbolKind.Method,
            Signature = "void MyMethod(int x)",
            Summary = "Does something useful.",
            OneLineSummary = "Does something.",
            ContentHash = "abc123",
            Embedding = embedding,
            IsUtility = false,
            SourceRangeStart = 10,
            SourceRangeEnd = 50,
        };

        Assert.Equal(id, symbol.Id);
        Assert.Equal(fileId, symbol.FileId);
        Assert.Equal(moduleId, symbol.ModuleId);
        Assert.Equal("MyMethod", symbol.Name);
        Assert.Equal("MyNamespace.MyClass.MyMethod", symbol.FullyQualifiedName);
        Assert.Equal(SymbolKind.Method, symbol.Kind);
        Assert.Equal("void MyMethod(int x)", symbol.Signature);
        Assert.Equal("Does something useful.", symbol.Summary);
        Assert.Equal("Does something.", symbol.OneLineSummary);
        Assert.Equal("abc123", symbol.ContentHash);
        Assert.Equal(embedding, symbol.Embedding);
        Assert.False(symbol.IsUtility);
        Assert.Equal(10, symbol.SourceRangeStart);
        Assert.Equal(50, symbol.SourceRangeEnd);
    }

    [Fact]
    public void Symbol_WithExpression_MutatesKind()
    {
        var original = new Symbol
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Name = "MyProp",
            Kind = SymbolKind.Property,
            IsUtility = false,
            SourceRangeStart = 0,
            SourceRangeEnd = 5,
        };

        var mutated = original with { Kind = SymbolKind.Field };

        Assert.Equal(SymbolKind.Field, mutated.Kind);
        Assert.Equal(original.Id, mutated.Id);
    }

    [Fact]
    public void Symbol_NullEmbedding_IsValid()
    {
        var symbol = new Symbol
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Name = "MyField",
            Kind = SymbolKind.Field,
            Embedding = null,
            IsUtility = true,
            SourceRangeStart = 1,
            SourceRangeEnd = 2,
        };

        Assert.Null(symbol.Embedding);
    }

    [Fact]
    public void Symbol_NullModuleId_IsValid()
    {
        var symbol = new Symbol
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            ModuleId = null,
            Name = "TopLevelFunc",
            Kind = SymbolKind.Function,
            IsUtility = false,
            SourceRangeStart = 0,
            SourceRangeEnd = 20,
        };

        Assert.Null(symbol.ModuleId);
    }

    [Fact]
    public void SymbolKind_ContainsExpectedValues()
    {
        var values = Enum.GetValues<SymbolKind>();

        Assert.Contains(SymbolKind.Namespace, values);
        Assert.Contains(SymbolKind.Class, values);
        Assert.Contains(SymbolKind.Struct, values);
        Assert.Contains(SymbolKind.Interface, values);
        Assert.Contains(SymbolKind.Enum, values);
        Assert.Contains(SymbolKind.Method, values);
        Assert.Contains(SymbolKind.Function, values);
        Assert.Contains(SymbolKind.Property, values);
        Assert.Contains(SymbolKind.Field, values);
    }
}
