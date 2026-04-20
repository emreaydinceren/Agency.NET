namespace Agency.Agentic.Console;

internal sealed class TextWriterChatOutput (TextWriter textWriter) : IChatOutput
{
    public void WriteLine() => this.WriteLine(string.Empty);

    public void WriteLine(string text) => this.WriteLine(null, text);

    public void WriteLine(string? colorName, string text)
    {
        textWriter.WriteLine(text);
    }

    public void Write(string? colorName, string text)
    {
        textWriter.Write(text);
    }

    public void Write(string text) => this.Write(null, text);

    public void WriteLineMarkdown(string text)
    {
        textWriter.WriteLine(text);
    }

    public void WriteMarkup(string text)
    {
        textWriter.WriteLine(text);
    }

    public void StartSpinner(string markup = "[yellow]Initializing warp drive...[/]")
    {
        // No spinner support in TextWriter, so we'll just write the markup as-is.
    }

    public void StopSpinner()
    {
        // No spinner support in TextWriter, so we'll just write a newline to indicate the spinner has stopped.
    }

    public void WriteLineMarkup(string text)
    {
        textWriter.WriteLine(text);
    }

    public void WriteMarkdownInBorderedPanel(string header, string text)
    {
        textWriter.WriteLine(text);
    }
}
