using Spectre.Console;
using Spectre.Console.Rendering;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for GFM pipe-table support in <see cref="MarkdownRenderer"/>.
/// The parser (<see cref="MarkdownRenderer.TryParseTable"/>) is pure and asserted
/// directly; the builder (<see cref="MarkdownRenderer.BuildTable"/>) is rendered through
/// a string-backed Spectre console so we can assert real box-drawing output appears
/// instead of the literal pipe characters the old renderer emitted.
/// </summary>
public sealed class MarkdownRendererTableTests
{
    [Fact]
    public void TryParseTable_ParsesHeadersRowsAndConsumesBlock()
    {
        string[] lines =
        [
            "| Name | CPU |",
            "| :--- | ---: |",
            "| pwsh.exe | 100 K |",
            "| code.exe | 200 K |",
        ];

        bool ok = MarkdownRenderer.TryParseTable(lines, 0, out MarkdownRenderer.ParsedTable? table, out int next);

        Assert.True(ok);
        Assert.Equal(["Name", "CPU"], table!.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["pwsh.exe", "100 K"], table.Rows[0]);
        Assert.Equal(["code.exe", "200 K"], table.Rows[1]);
        Assert.Equal(4, next);   // header + separator + 2 body rows
    }

    [Fact]
    public void TryParseTable_StopsAtBlankLine()
    {
        string[] lines =
        [
            "| A | B |",
            "| - | - |",
            "| 1 | 2 |",
            "",
            "a trailing paragraph",
        ];

        bool ok = MarkdownRenderer.TryParseTable(lines, 0, out MarkdownRenderer.ParsedTable? table, out int next);

        Assert.True(ok);
        Assert.Single(table!.Rows);
        Assert.Equal(3, next);   // the blank line terminates the table
    }

    [Fact]
    public void TryParseTable_AcceptsRowsWithoutOuterPipes()
    {
        string[] lines =
        [
            "Name | CPU",
            "--- | ---",
            "pwsh.exe | 100 K",
        ];

        bool ok = MarkdownRenderer.TryParseTable(lines, 0, out MarkdownRenderer.ParsedTable? table, out int next);

        Assert.True(ok);
        Assert.Equal(["Name", "CPU"], table!.Headers);
        Assert.Equal(["pwsh.exe", "100 K"], table.Rows[0]);
        Assert.Equal(3, next);
    }

    [Theory]
    [InlineData("a | pipe in prose", "but the next line is not a delimiter")]
    [InlineData("no pipe at all here", "| --- | --- |")]
    public void TryParseTable_RejectsNonTables(string first, string second)
    {
        string[] lines = [first, second];

        bool ok = MarkdownRenderer.TryParseTable(lines, 0, out MarkdownRenderer.ParsedTable? table, out int next);

        Assert.False(ok);
        Assert.Null(table);
        Assert.Equal(0, next);
    }

    [Fact]
    public void BuildTable_RendersBoxDrawingAndCellContent()
    {
        var parsed = new MarkdownRenderer.ParsedTable(
            ["Process Name", "Memory"],
            [["pwsh.exe", "100,000 K"], ["code.exe", "200,000 K"]]);

        string rendered = Render(MarkdownRenderer.BuildTable(parsed));

        Assert.Contains("Process Name", rendered);
        Assert.Contains("pwsh.exe", rendered);
        Assert.Contains("100,000 K", rendered);
        // A genuine Spectre table draws vertical rules (U+2502); the old renderer left
        // literal ASCII '|'. Asserting the box character proves the table actually rendered.
        Assert.Contains('│', rendered);
    }

    [Fact]
    public void BuildTable_PadsAndTruncatesRaggedRows()
    {
        var parsed = new MarkdownRenderer.ParsedTable(
            ["A", "B", "C"],
            [["1", "2"], ["x", "y", "z", "extra"]]);

        // Spectre throws if a row's cell count != column count, so a clean render
        // proves ragged rows were normalised to the 3-column header width.
        string rendered = Render(MarkdownRenderer.BuildTable(parsed));

        Assert.Contains("1", rendered);
        Assert.Contains("z", rendered);
        Assert.DoesNotContain("extra", rendered);   // 4th cell dropped to match 3 columns
    }

    [Fact]
    public void BuildTable_AppliesInlineMarkupInCells()
    {
        var parsed = new MarkdownRenderer.ParsedTable(
            ["Col"],
            [["**bold**"]]);

        // No exception (markup is balanced) and the asterisks are consumed by the
        // bold transform rather than printed literally.
        string rendered = Render(MarkdownRenderer.BuildTable(parsed));

        Assert.Contains("bold", rendered);
        Assert.DoesNotContain("**", rendered);
    }

    private static string Render(IRenderable renderable)
    {
        var writer = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);
        return writer.ToString();
    }
}
