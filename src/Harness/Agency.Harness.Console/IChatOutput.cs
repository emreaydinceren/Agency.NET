namespace Agency.Harness.Console;

internal interface IChatOutput
{
    void WriteLine();

    void WriteLine(string? colorName, string text);

    void WriteLine(string text);

    void Write(string? colorName, string text);

    void Write(string text);

    void WriteLineMarkdown(string text);

    void WriteMarkup(string text);

    void WriteLineMarkup(string text);

    void StartSpinner(string markup = "[yellow]Thinking...[/]");

    void StopSpinner();

    void WriteMarkdownInBorderedPanel(string header, string text);
}