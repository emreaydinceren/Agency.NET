using Agency.Harness.Console.Commands;
using Spectre.Console;
using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for the JSON-schema renderer behind the <c>/dump-context</c> command.
/// These do NOT spawn the binary or require LM Studio — they validate that the markup
/// the renderer emits is well-formed (balanced tags, brackets escaped) by feeding it to
/// Spectre's <see cref="Markup"/> parser, which throws on malformed input.
/// </summary>
public sealed class DumpContextSchemaRenderTests
{
    // A representative tool schema with nested objects, arrays, unions, and nulls —
    // the kind of input that previously rendered as an unreadable minified blob.
    private const string MemorizeSchema = """
        {"type":"object","properties":{"record":{"type":"object","properties":{
        "scope":{"type":["object","null"],"properties":{"userId":{"type":"string"},
        "sessionId":{"type":["string","null"]}},"required":["userId","sessionId"]},
        "key":{"type":["string","null"]},"tags":{"type":["array","null"],
        "items":{"type":"string"}}}}},"required":["record"]}
        """;

    /// <summary>
    /// Rendering a schema with nested objects, arrays, unions, and nulls must produce markup
    /// that Spectre's <see cref="Markup"/> parser accepts.
    /// </summary>
    [Fact]
    public void RenderJson_ProducesMarkupSpectreCanParse()
    {
        using var doc = JsonDocument.Parse(MemorizeSchema);

        string markup = DumpContextCommand.RenderJson(doc.RootElement);

        // The decisive check: Spectre throws if any tag is unbalanced or a literal
        // '[' / ']' slipped through unescaped. Construction succeeding == valid markup.
        var ex = Record.Exception(() => new Markup(markup));
        Assert.Null(ex);
    }

    /// <summary>
    /// Rendered output is pretty-printed across multiple indented lines with object keys and
    /// string values coloured, while the underlying JSON content survives markup stripping.
    /// </summary>
    [Fact]
    public void RenderJson_IsIndentedAndColoured()
    {
        using var doc = JsonDocument.Parse(MemorizeSchema);

        string markup = DumpContextCommand.RenderJson(doc.RootElement);

        Assert.Contains("\n", markup);              // pretty-printed across multiple lines
        Assert.Contains("  ", markup);              // nested keys are indented
        Assert.Contains("[blue]", markup);          // object keys are coloured
        Assert.Contains("[green]", markup);         // string values are coloured
        // After Spectre strips the markup, the plain text is still valid JSON content.
        Assert.Contains("userId", Markup.Remove(markup));
    }

    /// <summary>
    /// Degenerate JSON values — empty containers, primitives, and strings containing
    /// bracket characters — must still render as markup Spectre can parse.
    /// </summary>
    /// <param name="json">The raw JSON document to render.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"a [bracketed] string\"")]
    public void RenderJson_HandlesEdgeCases_WithoutMalformedMarkup(string json)
    {
        using var doc = JsonDocument.Parse(json);

        string markup = DumpContextCommand.RenderJson(doc.RootElement);

        Assert.Null(Record.Exception(() => new Markup(markup)));
    }
}
