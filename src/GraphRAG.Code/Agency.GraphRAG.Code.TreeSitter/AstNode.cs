using System.Text.Json;
namespace Agency.GraphRAG.Code.TreeSitter;

/// <summary>
/// Represents a tree-sitter AST node.
/// </summary>
/// <param name="Kind">The node kind.</param>
/// <param name="Text">The source text represented by the node when available.</param>
/// <param name="Range">The source range for the node.</param>
/// <param name="Children">The child nodes.</param>
/// <param name="FieldName">The optional tree-sitter field name.</param>
public sealed record AstNode(string Kind, string? Text, SourceRange? Range, IReadOnlyList<AstNode> Children, string? FieldName = null)
{
    /// <summary>
    /// Creates an <see cref="AstNode"/> from JSON.
    /// </summary>
    /// <param name="element">The JSON element.</param>
    /// <returns>The parsed node.</returns>
    public static AstNode FromJsonElement(JsonElement element)
    {
        string kind = GetString(element, "kind")
            ?? GetString(element, "type")
            ?? throw new JsonException("Tree-sitter node is missing a kind/type property.");

        string? text = GetString(element, "text") ?? GetString(element, "value");
        SourceRange? range = SourceRange.FromJsonElement(element);

        List<AstNode> children = [];
        if (TryGetProperty(element, "children", out JsonElement childArray) && childArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in childArray.EnumerateArray())
            {
                children.Add(FromJsonElement(child));
            }
        }

        string? fieldName = GetString(element, "fieldName");
        return new AstNode(kind, text, range, children, fieldName);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// Represents a source range.
/// </summary>
/// <param name="StartLine">The zero-based start line.</param>
/// <param name="StartColumn">The zero-based start column.</param>
/// <param name="EndLine">The zero-based end line.</param>
/// <param name="EndColumn">The zero-based end column.</param>
public sealed record SourceRange(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    /// <summary>
    /// Creates a <see cref="SourceRange"/> from JSON when available.
    /// </summary>
    /// <param name="element">The JSON element to inspect.</param>
    /// <returns>The parsed range, or <see langword="null"/> when none is present.</returns>
    public static SourceRange? FromJsonElement(JsonElement element)
    {
        if (TryGetProperty(element, "range", out JsonElement rangeElement) && rangeElement.ValueKind == JsonValueKind.Object)
        {
            int? startLine = GetInt(rangeElement, "startLine");
            int? startColumn = GetInt(rangeElement, "startColumn");
            int? endLine = GetInt(rangeElement, "endLine");
            int? endColumn = GetInt(rangeElement, "endColumn");
            if (startLine.HasValue && startColumn.HasValue && endLine.HasValue && endColumn.HasValue)
            {
                return new SourceRange(startLine.Value, startColumn.Value, endLine.Value, endColumn.Value);
            }
        }

        JsonElement? startPosition = GetPosition(element, "startPosition") ?? GetPosition(element, "startPoint");
        JsonElement? endPosition = GetPosition(element, "endPosition") ?? GetPosition(element, "endPoint");
        if (startPosition.HasValue && endPosition.HasValue)
        {
            int? startLine = GetInt(startPosition.Value, "row") ?? GetInt(startPosition.Value, "line");
            int? startColumn = GetInt(startPosition.Value, "column");
            int? endLine = GetInt(endPosition.Value, "row") ?? GetInt(endPosition.Value, "line");
            int? endColumn = GetInt(endPosition.Value, "column");
            if (startLine.HasValue && startColumn.HasValue && endLine.HasValue && endColumn.HasValue)
            {
                return new SourceRange(startLine.Value, startColumn.Value, endLine.Value, endColumn.Value);
            }
        }

        int? inlineStartLine = GetInt(element, "startLine");
        int? inlineStartColumn = GetInt(element, "startColumn");
        int? inlineEndLine = GetInt(element, "endLine");
        int? inlineEndColumn = GetInt(element, "endColumn");
        if (inlineStartLine.HasValue && inlineStartColumn.HasValue && inlineEndLine.HasValue && inlineEndColumn.HasValue)
        {
            return new SourceRange(inlineStartLine.Value, inlineStartColumn.Value, inlineEndLine.Value, inlineEndColumn.Value);
        }

        return null;
    }

    private static JsonElement? GetPosition(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out JsonElement position) && position.ValueKind == JsonValueKind.Object
            ? position
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int intValue))
        {
            return intValue;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
