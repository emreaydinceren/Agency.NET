namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="HtmlTextExtractor"/>.</summary>
public sealed class HtmlTextExtractorTests
{
    /// <summary>Verifies that scripts and styles are dropped, entities decoded and block elements kept apart.</summary>
    [Fact]
    public void Extract_StripsMarkupAndKeepsReadableText()
    {
        const string html = """
            <html><head><title>Release Guide</title><style>p { color: red; }</style></head>
            <body>
              <script>if (a < b) { track(); }</script>
              <h1>Publishing</h1><p>Tag &amp; push&nbsp;the   release.</p>
              <ul><li>One</li><li>Two</li></ul>
              <noscript>enable js</noscript>
            </body></html>
            """;

        string text = HtmlTextExtractor.Extract(html);

        Assert.Equal("Release Guide\n\nPublishing\n\nTag & push the release.\n\nOne\n\nTwo", text);
    }
}
