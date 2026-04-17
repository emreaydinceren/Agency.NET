namespace Agency.Agentic.Console;

using Spectre.Console;
using Spectre.Console.Rendering;

internal sealed class ConsoleOutput : IChatOutput
{
    public static IChatOutput Instance { get; private set; } = new ConsoleOutput();

    public void WriteLineMarkdown(string text)
    {
        MarkdownRenderer.Print(text);
    }

    public void WriteMarkup(string text)
    {
        
        this.Write(new Markup(text));
    }

    private void Write(IRenderable renderable)
    {
        AnsiConsole.Write(renderable);
    }

    public void WriteLineMarkup(string text)
    {
        WriteMarkup(text + Environment.NewLine);
    }

    public void WriteLine() =>
        this.WriteLine(string.Empty);

    public void WriteLine(string text) => this.WriteLine(null, text);


    public void WriteLine(string? colorName, string text)
    {
        if (!string.IsNullOrWhiteSpace(colorName))
        {
            WriteLineMarkup($"[{colorName}]{Markup.Escape(text)}[/]");
        }
        else
        {
            WriteLineMarkup(Markup.Escape(text));
        }
    }

    public void Write(string text) => this.Write(null, text);

    public void Write(string? colorName, string text)
    {
        if (!string.IsNullOrWhiteSpace(colorName))
        {
            WriteMarkup($"[{colorName}]{Markup.Escape(text)}[/]");
        }
        else
        {
            WriteMarkup(Markup.Escape(text));
        }
    }

    public void WriteMarkdownInBorderedPanel(string header, string text)
    {
        var panel = new Panel(text)
        {
            Header = new PanelHeader(header, Justify.Left),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0),
            BorderStyle = new Style(Color.Gray)
        };
        this.Write(panel);
    }

    private static string[] Frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private Thread? spinnerThread;
    private volatile bool spinnerRunning;

    public void StartSpinner(string markup = "[yellow]Thinking..[/]")
    {
        if (System.Console.IsOutputRedirected || !AnsiConsole.Console.Profile.Capabilities.Interactive)
        {
            return;
        }

        spinnerRunning = true;

        spinnerThread = new Thread(() =>
        {
            int frameIndex = 0;

            while (spinnerRunning)
            {
                System.Console.CursorVisible = false;
                var (cursorLeft, cursorTop) = (System.Console.CursorLeft, System.Console.CursorTop);
                string frame = Frames[frameIndex % Frames.Length];
                var text = string.Concat(frame, " ", markup);
                this.WriteMarkup(text);
                System.Console.Out.Flush();
                AnsiConsole.Console.Cursor.MoveLeft(text.Length);
                frameIndex++;
                Thread.Sleep(120);
            }
            System.Console.CursorVisible = true;
        })
        {
            IsBackground = true,
            Name = "ConsoleSpinner"
        };

        spinnerThread.Start();
    }

    public void StopSpinner()
    {
        if (System.Console.IsOutputRedirected || !AnsiConsole.Console.Profile.Capabilities.Interactive)
        {
            return;
        }

        spinnerRunning = false;
        spinnerThread?.Join(timeout: TimeSpan.FromMilliseconds(500));
        spinnerThread = null;
        System.Console.Write("\r" + new string(' ', 20) + "\r"); // Clear the line
    }
}
