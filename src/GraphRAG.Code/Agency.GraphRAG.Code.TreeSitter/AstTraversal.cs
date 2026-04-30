namespace Agency.GraphRAG.Code.TreeSitter;

/// <summary>
/// Provides helpers for traversing tree-sitter ASTs.
/// </summary>
public static class AstTraversal
{
    /// <summary>
    /// Finds nodes of a given kind in depth-first order.
    /// </summary>
    /// <param name="root">The root node.</param>
    /// <param name="kind">The node kind to match.</param>
    /// <returns>The matching nodes.</returns>
    public static IEnumerable<AstNode> FindNodesOfKind(AstNode root, string kind)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (string.Equals(root.Kind, kind, StringComparison.Ordinal))
        {
            yield return root;
        }

        foreach (AstNode child in root.Children)
        {
            foreach (AstNode match in FindNodesOfKind(child, kind))
            {
                yield return match;
            }
        }
    }

    /// <summary>
    /// Gets the first identifier text under a node.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns>The identifier text when present; otherwise <see langword="null"/>.</returns>
    public static string? GetIdentifier(AstNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (IsIdentifierKind(node.Kind) && !string.IsNullOrWhiteSpace(node.Text))
        {
            return node.Text;
        }

        foreach (AstNode child in node.Children)
        {
            string? identifier = GetIdentifier(child);
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return identifier;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the source range for a node.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The node source range when available.</returns>
    public static SourceRange? GetSourceRange(AstNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Range;
    }

    private static bool IsIdentifierKind(string kind)
    {
        return kind is "identifier" or "property_identifier" or "type_identifier";
    }
}
