
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
                output.WriteLine();
                output.WriteLine(toClear);
                AnsiConsole.Console.Write(rule);
                AnsiConsole.Cursor.MoveUp(2);
                AnsiConsole.Cursor.MoveRight(leftMargin);
                break;

                case ConsoleKey.Enter:

                string result = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(result))
                {
                    this._history.Add(result);
                }

                // Collapse the rule/input/rule box into a single highlighted echo line.
                // Use RELATIVE cursor moves plus an erase-to-end-of-display rather than
                // absolute SetPosition: absolute row numbers go stale the instant the
                // terminal scrolls (which happens whenever the prompt is drawn near the
                // bottom of a populated window), leaving blank gaps and a duplicated
                // prompt behind. Relative moves are unaffected by scrolling.
                System.Console.Write('\r');               // column 0 of the current input row
                AnsiConsole.Cursor.MoveUp(inputRowCount);  // up to the top rule row
                System.Console.Write("\u001b[0J");         // erase top rule, input rows, bottom rule
                output.WriteLineMarkup($"[white on Gray19]{Markup.Remove(markup)}{result}[/]");
                return result;

                case ConsoleKey.Backspace when buffer.Length > 0 && inputRowCount > 1 && System.Console.CursorLeft == leftMargin:
                AnsiConsole.Cursor.Show();
                AnsiConsole.Cursor.SetPosition(0, initialCursorTop + inputRowCount + 2);
                output.Write(toClear);
                inputRowCount--;
                var newLineLength = Environment.NewLine.Length;
                buffer.Remove(buffer.Length - newLineLength, newLineLength);
                var lengthOfLastLine = buffer.ToString().Split('\n').Last().Length;
                AnsiConsole.Cursor.SetPosition(0, initialCursorTop + inputRowCount + 2);
                AnsiConsole.Console.Write(rule);
                AnsiConsole.Cursor.SetPosition(leftMargin + lengthOfLastLine + 1, initialCursorTop + inputRowCount + 1);
                break;

                case ConsoleKey.Backspace when buffer.Length > 0:
                AnsiConsole.Cursor.Show();
                buffer.Remove(buffer.Length - 1, 1);
                output.Write("\b \b");
                break;

                case ConsoleKey.LeftArrow when System.Console.CursorLeft > leftMargin:
                AnsiConsole.Cursor.MoveLeft();
                break;

                case ConsoleKey.Home:
                AnsiConsole.Cursor.SetPosition(leftMargin + 1, System.Console.CursorTop + 1);
                break;

                case ConsoleKey.End:
                AnsiConsole.Cursor.SetPosition(buffer.Length + leftMargin + 1, System.Console.CursorTop + 1);
                break;

                case ConsoleKey.RightArrow when System.Console.CursorLeft < buffer.Length + leftMargin:
                AnsiConsole.Cursor.MoveRight();
                break;

                case ConsoleKey.Escape:
                ReplaceBufferLine(buffer, "");
                buffer.Clear();
                break;

                case ConsoleKey.UpArrow:
                if (inputRowCount > 1 && System.Console.CursorTop > initialCursorTop + 1)
                {
                    AnsiConsole.Cursor.MoveUp();
                }
                else if (historyIndex > 0)
                {
                    historyIndex--;
                    ReplaceBufferLine(buffer, this._history[historyIndex]);
                    buffer.Clear();
                    buffer.Append(this._history[historyIndex]);
                }

                break;

                case ConsoleKey.DownArrow:
                if (inputRowCount > 1 && System.Console.CursorTop < initialCursorTop + inputRowCount)
                {
                    AnsiConsole.Cursor.MoveDown();
                }
                else if (historyIndex < this._history.Count - 1)
                {
                    historyIndex++;
                    ReplaceBufferLine(buffer, this._history[historyIndex]);
                    buffer.Clear();
                    buffer.Append(this._history[historyIndex]);
                }
                else
                {
                    historyIndex = this._history.Count;
                    ReplaceBufferLine(buffer, "");
                    buffer.Clear();
                }

                break;

                default:
                if (key.KeyChar == '/' && buffer.Length == 0)
                {
                    var commands = CommandRegistry.Commands.Select(cmd => new ConsolePickerRow(cmd.CommandText, cmd.Description)).ToList();
                    output.WriteLine();
                    string? picked = ConsolePicker.Show(commands, 0);
                    output.WriteMarkup(markup);
                    if (picked is not null)
                    {
                        buffer.Clear();
                        buffer.Append(picked);
                        output.Write(picked);
                    }
                }
                else if (key.KeyChar >= 32)
                {
                    buffer.Append(key.KeyChar);
                    output.Write(key.KeyChar.ToString());
                }

                break;
            }
        }
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
