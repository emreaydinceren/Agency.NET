using Agency.GraphRAG.Code.TreeSitter;

namespace Agency.GraphRAG.Code.Test.TreeSitter;

/// <summary>
/// Tests for <see cref="AstTraversal"/>.
/// </summary>
public sealed class AstTraversalTests
{
    [Fact]
    public void FindNodesOfKind_ReturnsMatchesInDepthFirstOrder()
    {
        AstNode root = CreateFixtureAst();

        AstNode[] matches = AstTraversal.FindNodesOfKind(root, "method_declaration").ToArray();

        Assert.Equal(2, matches.Length);
        Assert.Equal(["Run", "Stop"], matches.Select(match => AstTraversal.GetIdentifier(match) ?? string.Empty).ToArray());
    }

    [Fact]
    public void GetIdentifier_ReturnsNestedIdentifierText()
    {
        AstNode method = AstTraversal.FindNodesOfKind(CreateFixtureAst(), "method_declaration").First();

        string? identifier = AstTraversal.GetIdentifier(method);

        Assert.Equal("Run", identifier);
    }

    [Fact]
    public void GetSourceRange_ReturnsNodeRange()
    {
        AstNode classNode = AstTraversal.FindNodesOfKind(CreateFixtureAst(), "class_declaration").Single();

        SourceRange? range = AstTraversal.GetSourceRange(classNode);

        Assert.NotNull(range);
        Assert.Equal(new SourceRange(1, 0, 8, 1), range);
    }

    private static AstNode CreateFixtureAst()
    {
        return new AstNode(
            "compilation_unit",
            null,
            new SourceRange(0, 0, 9, 0),
            [
                new AstNode(
                    "class_declaration",
                    null,
                    new SourceRange(1, 0, 8, 1),
                    [
                        new AstNode("identifier", "Worker", new SourceRange(1, 6, 1, 12), []),
                        new AstNode(
                            "method_declaration",
                            null,
                            new SourceRange(3, 4, 4, 5),
                            [
                                new AstNode("identifier", "Run", new SourceRange(3, 9, 3, 12), []),
                            ]),
                        new AstNode(
                            "method_declaration",
                            null,
                            new SourceRange(5, 4, 6, 5),
                            [
                                new AstNode("identifier", "Stop", new SourceRange(5, 9, 5, 13), []),
                            ]),
                    ]),
            ]);
    }
}
