namespace Agency.Agentic.Console;

using Spectre.Console;
using Spectre.Console.Rendering;
using System.Threading.Channels;

internal sealed class ConsoleOutput : IChatOutput
{
    enum State
    {
        Stopped,
        Running,
        Spinner,
    }

    private Channel<IRenderable> mainChannel = Channel.CreateUnbounded<IRenderable>();
    private Channel<IRenderable> spinnerChannel = Channel.CreateUnbounded<IRenderable>();
    private Task? consumerTask; 
    private Task? spinnerTask;
    private CancellationTokenSource? spinnerCTS;
    private CancellationTokenSource? consumerCTS;

    private volatile State state = State.Stopped;

    public static IChatOutput Instance { get; private set; } = new ConsoleOutput();

    public void WriteLineMarkdown(string text)
    {
        MarkdownRenderer.Print(text);
    }

    public void WriteMarkup(string text)
    {
        this.EnsureConsumerStarted();
        this.mainChannel.Writer.TryWrite(new Markup(text));
    }

    private void Write(IRenderable renderable)
    {
        this.EnsureConsumerStarted();
        this.mainChannel.Writer.TryWrite(renderable);
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

    private void EnsureConsumerStarted()
    {
        if (this.state != State.Running)
        {
            StartConsumer();
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var reader = this.mainChannel.Reader;

        try
        {
            await foreach (IRenderable renderable in reader.ReadAllAsync(cancellationToken))
            {
                if (state == State.Spinner)
                {
                    this.spinnerChannel.Writer.TryWrite(renderable);
                }
                else
                {
                    AnsiConsole.Write(renderable);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on cancellation, ignore
        }
    }

    public void StartSpinner(string markup = "[yellow]Thinking..[/]")
    {
        var originalState = Interlocked.CompareExchange(ref state, State.Spinner, State.Running);

        if (originalState != State.Running)
        {
            StartConsumer();
            StartSpinner(markup);
        }

        spinnerCTS = new CancellationTokenSource();

        spinnerTask = AnsiConsole.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Default)
            .StartAsync(markup, async ctx =>
            {
                var reader = this.spinnerChannel.Reader;

                Interlocked.Exchange(ref state, State.Spinner);

                try
                {
                    await foreach (IRenderable renderable in reader.ReadAllAsync(spinnerCTS.Token))
                    {
                        AnsiConsole.Write(renderable);

                        if (spinnerCTS.Token.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // expected on cancellation, ignore
                }
                finally
                {
                    Interlocked.Exchange(ref state, State.Running);
                }
            });
    }

    private void StartConsumer()
    {
        this.consumerCTS = new CancellationTokenSource();
        this.consumerTask = Task.Factory.StartNew(() =>
            this.ConsumeAsync(this.consumerCTS.Token),
            this.consumerCTS.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        Interlocked.Exchange(ref state, State.Running);
    }

    public void StopSpinner()
    {
        if (spinnerCTS != null)
        {
            spinnerCTS.Cancel();
            spinnerTask?.Wait();
            spinnerTask = null;
            spinnerCTS.Dispose();
            spinnerCTS = null;
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
}
