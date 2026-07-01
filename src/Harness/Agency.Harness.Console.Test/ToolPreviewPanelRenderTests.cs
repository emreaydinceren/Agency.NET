using Spectre.Console;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Guards the tool-preview panel rendering against Spectre markup injection. Tool result content and
/// serialized arguments are arbitrary data that can contain literal '[' / ']' — most notably an empty JSON
/// array <c>[]</c> — which Spectre would otherwise parse as a (malformed) markup tag. The decisive check is
/// feeding the production-built markup to <see cref="Panel"/>, the exact constructor that threw
/// "Could not find color or style ''" on the unescaped <c>[gray][][/]</c>.
/// </summary>
public sealed class ToolPreviewPanelRenderTests
{
    /// <summary>
    /// Arbitrary tool-preview content — including bracket characters that resemble Spectre
    /// markup tags — must escape cleanly into a <see cref="Panel"/> without throwing.
    /// </summary>
    /// <param name="content">The raw, untrusted content to format.</param>
    [Theory]
    [InlineData("[]")]                       // the empty JSON array that crashed
    [InlineData("{}")]
    [InlineData("[gray]")]                    // a bare opening tag
    [InlineData("a [bracketed] value")]
    [InlineData("[red]injected[/]")]          // an attempt to inject real markup
    [InlineData("unbalanced ] bracket")]
    public void FormatGrayPreview_ProducesPanelSpectreCanParse(string content)
    {
        string markup = ConsoleChatSession.FormatGrayPreview(content);

        // Panel's constructor parses its text as markup; it throws on malformed/empty tags.
        Assert.Null(Record.Exception(() => new Panel(markup)));
    }

    /// <summary>
    /// Bracket data in the content is escaped, not interpreted as a markup tag, so it survives
    /// intact once the markup is stripped back out.
    /// </summary>
    [Fact]
    public void FormatGrayPreview_PreservesContentAfterMarkupIsStripped()
    {
        string markup = ConsoleChatSession.FormatGrayPreview("[]");

        // The bracket data survives — it was escaped, not interpreted as a tag.
        Assert.Equal("[]", Markup.Remove(markup));
    }

    /// <summary>
    /// Pins the original regression: wrapping unescaped bracket content in a <see cref="Panel"/>
    /// throws, proving the escaping step in <see cref="ConsoleChatSession.FormatGrayPreview"/> is load-bearing.
    /// </summary>
    [Fact]
    public void UnescapedPreview_Throws_DemonstratingTheEscapeIsLoadBearing()
    {
        // Pinning the original bug: the same wrapping without escaping is what blew up.
        Assert.ThrowsAny<Exception>(() => new Panel("[gray][][/]"));
    }
}
