using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Domain;

/// <summary>
/// Tests for <see cref="UnresolvedCallSite"/>.
/// </summary>
public sealed class UnresolvedCallSiteTests
{
    [Fact]
    public void UnresolvedCallSite_ConstructionWithAllFields_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var sourceSymbolId = Guid.NewGuid();
        var sourceFileId = Guid.NewGuid();

        var callSite = new UnresolvedCallSite
        {
            Id = id,
            SourceSymbolId = sourceSymbolId,
            SourceFileId = sourceFileId,
            Identifier = "Console.WriteLine",
            Scope = "System",
            LlmExtractedTarget = "System.Console.WriteLine(string)",
        };

        Assert.Equal(id, callSite.Id);
        Assert.Equal(sourceSymbolId, callSite.SourceSymbolId);
        Assert.Equal(sourceFileId, callSite.SourceFileId);
        Assert.Equal("Console.WriteLine", callSite.Identifier);
        Assert.Equal("System", callSite.Scope);
        Assert.Equal("System.Console.WriteLine(string)", callSite.LlmExtractedTarget);
    }

    [Fact]
    public void UnresolvedCallSite_NullScope_IsValid()
    {
        var callSite = new UnresolvedCallSite
        {
            Id = Guid.NewGuid(),
            SourceSymbolId = Guid.NewGuid(),
            SourceFileId = Guid.NewGuid(),
            Identifier = "Foo",
            Scope = null,
        };

        Assert.Null(callSite.Scope);
    }

    [Fact]
    public void UnresolvedCallSite_NullLlmExtractedTarget_IsValid()
    {
        var callSite = new UnresolvedCallSite
        {
            Id = Guid.NewGuid(),
            SourceSymbolId = Guid.NewGuid(),
            SourceFileId = Guid.NewGuid(),
            Identifier = "Bar",
            LlmExtractedTarget = null,
        };

        Assert.Null(callSite.LlmExtractedTarget);
    }

    [Fact]
    public void UnresolvedCallSite_WithExpression_MutatesIdentifier()
    {
        var original = new UnresolvedCallSite
        {
            Id = Guid.NewGuid(),
            SourceSymbolId = Guid.NewGuid(),
            SourceFileId = Guid.NewGuid(),
            Identifier = "OldCall",
        };

        var mutated = original with { Identifier = "NewCall" };

        Assert.Equal("NewCall", mutated.Identifier);
        Assert.Equal(original.Id, mutated.Id);
        Assert.Equal(original.SourceSymbolId, mutated.SourceSymbolId);
    }
}
