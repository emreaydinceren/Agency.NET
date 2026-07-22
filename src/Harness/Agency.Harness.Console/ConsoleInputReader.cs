
using Agency.Harness.Console.Commands;
using Spectre.Console;
using System.Text;

namespace Agency.Harness.Console;
/// <summary>
/// Handles interactive terminal input for the console chat REPL, including
/// history navigation, cursor management, and slash-command autocompletion.
/// </summary>
internal sealed class ConsoleInputReader(IChatOutput output)
{
    private readonly List<string> _history = [];

    /// <summary>
    /// Reads a line of input from the console, rendering <paramref name="markup"/> as the prompt.
    /// Returns <see langword="null"/> when the user cancels via <paramref name="ct"/>.
    /// </summary>
    internal async Task<string?> ReadLineAsync(string markup, CancellationToken ct)
    {
        var console = AnsiConsole.Console;

        if (!console.Profile.Capabilities.Interactive)
        {
            try
            {
                return await System.Console.In.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                output.WriteLine();
                return null;
            }
        }

        int initialCursorTop = System.Console.CursorTop;
        var rule = new Rule()
        {
            Border = BoxBorder.Square,
            Style = new Style(foreground: Color.Gray30)
        };

        AnsiConsole.Console.Write(rule);
        output.WriteLineMarkup(markup);
        var leftMargin = Markup.Remove(markup).Length;
        AnsiConsole.Console.Write(rule);

        AnsiConsole.Cursor.MoveUp(2);
        AnsiConsole.Cursor.MoveRight(leftMargin);

        var buffer = new StringBuilder();
        int historyIndex = this._history.Count;
        int cursorIndex = 0;

        // Collapses the rule/input/rule box into a single highlighted echo line and
        // returns result, as if the user had typed it and pressed Enter. Uses RELATIVE
        // cursor moves plus an erase-to-end-of-display rather than absolute SetPosition:
        // absolute row numbers go stale the instant the terminal scrolls (which happens
        // whenever the prompt is drawn near the bottom of a populated window), leaving
        // blank gaps and a duplicated prompt behind. Relative moves are unaffected by scrolling.
        string Submit(string result)
        {
            if (!string.IsNullOrWhiteSpace(result))
            {
                this._history.Add(result);
            }

            System.Console.Write('\r');                                  // column 0 of the current input row
            AnsiConsole.Cursor.MoveUp(result.Split('\n').Length);         // up to the top rule row
            System.Console.Write("\u001b[0J");                           // erase top rule, input rows, bottom rule
            output.WriteLineMarkup($"[white on Gray19]{Markup.Remove(markup)}{result}[/]");
            return result;
        }

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                output.WriteLine();
                return null;
            }

            if (!console.Input.IsKeyAvailable())
            {
                try
                {
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException)
                {
                    output.WriteLine();
                    return null;
                }

                continue;
            }

            ConsoleKeyInfo? keyInfo = console.Input.ReadKey(intercept: true);

            if (keyInfo is null)
            {
                continue;
            }

            ConsoleKeyInfo key = keyInfo.Value;
            string toClear = new string(' ', console.Profile.Width);

            int inputRowCount = buffer.ToString().Split('\n').Length;

            switch (key.Key)
            {
                case ConsoleKey.Enter when (keyInfo.Value.Modifiers & ConsoleModifiers.Control) != 0:
                buffer.Append(Environment.NewLine);
                cursorIndex = buffer.Length;
                output.WriteLine();
                output.WriteLine(toClear);
                AnsiConsole.Console.Write(rule);
                AnsiConsole.Cursor.MoveUp(2);
                AnsiConsole.Cursor.MoveRight(leftMargin);
                break;

                case ConsoleKey.Enter:
                return Submit(buffer.ToString());

                case ConsoleKey.Backspace when buffer.Length > 0 && inputRowCount > 1 && System.Console.CursorLeft == leftMargin:
                AnsiConsole.Cursor.Show();
                AnsiConsole.Cursor.SetPosition(0, initialCursorTop + inputRowCount + 2);
                output.Write(toClear);
                inputRowCount--;
                var newLineLength = Environment.NewLine.Length;
                buffer.Remove(buffer.Length - newLineLength, newLineLength);
                cursorIndex = buffer.Length;
                var lengthOfLastLine = buffer.ToString().Split('\n').Last().Length;
                AnsiConsole.Cursor.SetPosition(0, initialCursorTop + inputRowCount + 2);
                AnsiConsole.Console.Write(rule);
                AnsiConsole.Cursor.SetPosition(leftMargin + lengthOfLastLine + 1, initialCursorTop + inputRowCount + 1);
                break;

                case ConsoleKey.Backspace when buffer.Length > 0 && cursorIndex > 0:
                AnsiConsole.Cursor.Show();
                {
                    string textBeforeBackspace = buffer.ToString();
                    string tail = textBeforeBackspace.Substring(cursorIndex, RowEndIndex(textBeforeBackspace, cursorIndex) - cursorIndex);
                    buffer.Remove(cursorIndex - 1, 1);
                    cursorIndex--;
                    AnsiConsole.Cursor.MoveLeft();
                    output.Write(tail + " ");
                    AnsiConsole.Cursor.MoveLeft(tail.Length + 1);
                }

                break;

                case ConsoleKey.Delete when cursorIndex < RowEndIndex(buffer.ToString(), cursorIndex):
                AnsiConsole.Cursor.Show();
                {
                    string textBeforeDelete = buffer.ToString();
                    string tail = textBeforeDelete.Substring(cursorIndex + 1, RowEndIndex(textBeforeDelete, cursorIndex) - (cursorIndex + 1));
                    buffer.Remove(cursorIndex, 1);
                    output.Write(tail + " ");
                    AnsiConsole.Cursor.MoveLeft(tail.Length + 1);
                }

                break;

                case ConsoleKey.LeftArrow when cursorIndex > RowStartIndex(buffer.ToString(), cursorIndex):
                AnsiConsole.Cursor.MoveLeft();
                cursorIndex--;
                break;

                case ConsoleKey.Home:
                cursorIndex = RowStartIndex(buffer.ToString(), cursorIndex);
                AnsiConsole.Cursor.SetPosition(leftMargin + 1, System.Console.CursorTop + 1);
                break;

                case ConsoleKey.End:
                {
                    string text = buffer.ToString();
                    int rowStart = RowStartIndex(text, cursorIndex);
                    int rowEnd = RowEndIndex(text, cursorIndex);
                    cursorIndex = rowEnd;
                    AnsiConsole.Cursor.SetPosition(leftMargin + (rowEnd - rowStart) + 1, System.Console.CursorTop + 1);
                }

                break;

                case ConsoleKey.RightArrow when cursorIndex < RowEndIndex(buffer.ToString(), cursorIndex):
                AnsiConsole.Cursor.MoveRight();
                cursorIndex++;
                break;

                case ConsoleKey.Escape:
                ReplaceBufferLine(buffer, "");
                buffer.Clear();
                cursorIndex = 0;
                break;

                case ConsoleKey.UpArrow:
                if (inputRowCount > 1 && System.Console.CursorTop > initialCursorTop + 1)
                {
                    string text = buffer.ToString();
                    int col = cursorIndex - RowStartIndex(text, cursorIndex);
                    int targetRow = System.Console.CursorTop - (initialCursorTop + 1) - 1;
                    AnsiConsole.Cursor.MoveUp();
                    int newCursorIndex = IndexFromRowCol(text, targetRow, col);
                    int newCol = newCursorIndex - RowStartIndex(text, newCursorIndex);
                    if (newCol != col)
                    {
                        AnsiConsole.Cursor.MoveLeft(col - newCol);
                    }

                    cursorIndex = newCursorIndex;
                }
                else if (historyIndex > 0)
                {
                    historyIndex--;
                    ReplaceBufferLine(buffer, this._history[historyIndex]);
                    buffer.Clear();
                    buffer.Append(this._history[historyIndex]);
                    cursorIndex = buffer.Length;
                }

                break;

                case ConsoleKey.DownArrow:
                if (inputRowCount > 1 && System.Console.CursorTop < initialCursorTop + inputRowCount)
                {
                    string text = buffer.ToString();
                    int col = cursorIndex - RowStartIndex(text, cursorIndex);
                    int targetRow = System.Console.CursorTop - (initialCursorTop + 1) + 1;
                    AnsiConsole.Cursor.MoveDown();
                    int newCursorIndex = IndexFromRowCol(text, targetRow, col);
                    int newCol = newCursorIndex - RowStartIndex(text, newCursorIndex);
                    if (newCol != col)
                    {
                        AnsiConsole.Cursor.MoveLeft(col - newCol);
                    }

                    cursorIndex = newCursorIndex;
                }
                else if (historyIndex < this._history.Count - 1)
                {
                    historyIndex++;
                    ReplaceBufferLine(buffer, this._history[historyIndex]);
                    buffer.Clear();
                    buffer.Append(this._history[historyIndex]);
                    cursorIndex = buffer.Length;
                }
                else
                {
                    historyIndex = this._history.Count;
                    ReplaceBufferLine(buffer, "");
                    buffer.Clear();
                    cursorIndex = 0;
                }

                break;

                default:
                if (key.KeyChar == '/' && buffer.Length == 0)
                {
                    var commands = CommandRegistry.Commands
                        .Select(cmd => cmd.ArgumentHint is not null
                            ? new ConsolePickerRow(cmd.CommandText, cmd.ArgumentHint, cmd.Description)
                            : new ConsolePickerRow(cmd.CommandText, cmd.Description))
                        .ToList();
                    output.WriteLine();
                    string? picked = ConsolePicker.Show(commands, 0);
                    output.WriteMarkup(markup);
                    if (picked is not null)
                    {
                        buffer.Clear();
                        buffer.Append(picked);
                        output.Write(picked);

                        // Commands that take an argument need the buffer left open so the
                        // user can type it; argument-less commands submit immediately.
                        bool requiresArgument = CommandRegistry.Commands
                            .Any(cmd => cmd.CommandText == picked && cmd.ArgumentHint is not null);
                        if (requiresArgument)
                        {
                            buffer.Append(' ');
                            output.Write(" ");
                        }
                        else
                        {
                            return Submit(picked);
                        }

                        cursorIndex = buffer.Length;
                    }
                }
                else if (key.KeyChar >= 32)
                {
                    string text = buffer.ToString();
                    string tail = text.Substring(cursorIndex, RowEndIndex(text, cursorIndex) - cursorIndex);
                    buffer.Insert(cursorIndex, key.KeyChar);
                    cursorIndex++;
                    output.Write(key.KeyChar + tail);
                    if (tail.Length > 0)
                    {
                        AnsiConsole.Cursor.MoveLeft(tail.Length);
                    }
                }

                break;
            }
        }
    }

    /// <summary>Index of the start of the row (delimited by <see cref="Environment.NewLine"/>) containing <paramref name="cursor"/>.</summary>
    private static int RowStartIndex(string text, int cursor)
    {
        string newLine = Environment.NewLine;
        int start = 0;
        int i = 0;
        while (i <= cursor - newLine.Length)
        {
            if (string.CompareOrdinal(text, i, newLine, 0, newLine.Length) == 0)
            {
                start = i + newLine.Length;
                i += newLine.Length;
            }
            else
            {
                i++;
            }
        }

        return start;
    }

    /// <summary>Index of the end of the row (delimited by <see cref="Environment.NewLine"/>) containing <paramref name="cursor"/>.</summary>
    private static int RowEndIndex(string text, int cursor)
    {
        int idx = text.IndexOf(Environment.NewLine, cursor, StringComparison.Ordinal);
        return idx < 0 ? text.Length : idx;
    }

    /// <summary>Maps a (row, column) pair back to a buffer index, clamping <paramref name="col"/> to the target row's length.</summary>
    private static int IndexFromRowCol(string text, int row, int col)
    {
        string newLine = Environment.NewLine;
        int i = 0;
        for (int r = 0; r < row; r++)
        {
            int found = text.IndexOf(newLine, i, StringComparison.Ordinal);
            if (found < 0)
            {
                return text.Length;
            }

            i = found + newLine.Length;
        }

        int rowEnd = RowEndIndex(text, i);
        return i + Math.Min(col, rowEnd - i);
    }

    private void ReplaceBufferLine(StringBuilder current, string replacement)
    {
        int currentLen = current.Length;
        AnsiConsole.Cursor.MoveLeft(currentLen);
        output.Write(replacement);
        int overflow = currentLen - replacement.Length;
        if (overflow > 0)
        {
            output.Write(new string(' ', overflow));
            AnsiConsole.Cursor.MoveLeft(overflow);
        }
    }
}
