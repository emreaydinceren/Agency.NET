namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Agentic.Console.Commands;
using Agency.Agentic.Contexts;
using Agency.Llm.Common.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

internal sealed class ConsoleChatSession
{
    public static readonly string ActivitySourceName = "Agency.Agentic.Console";
    public static readonly string MeterName = "Agency.Agentic.Console";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _sessionCounter = _meter.CreateCounter<long>(
        "agent.console.sessions",
        description: "Total number of console chat sessions");

    private static readonly Counter<long> _sessionErrorCounter = _meter.CreateCounter<long>(
        "agent.console.errors",
        description: "Total number of failed console chat sessions");

    private static readonly Counter<long> _commandCounter = _meter.CreateCounter<long>(
        "agent.console.commands",
        description: "Total number of slash commands executed");

    private static readonly Counter<long> _sessionTurnCounter = _meter.CreateCounter<long>(
        "agent.console.turns",
        description: "Total number of successful console chat turns");

    private static readonly Counter<long> _sessionTokenCounter = _meter.CreateCounter<long>(
        "agent.console.tokens",
        description: "Total number of tokens observed by the console session");

    private static readonly Histogram<double> _sessionDurationHistogram = _meter.CreateHistogram<double>(
        "agent.console.session.duration",
        unit: "ms",
        description: "Duration of a console chat session in milliseconds");

    private readonly CommandManager commandManager;
    private readonly IChatOutput output;
    private readonly AgentOptions _options;
    private readonly List<string> _history = [];
    private readonly ILogger<ConsoleChatSession> _logger;
    private readonly ToolContext toolContext;
    private Agent _agent;

    internal IServiceProvider ServiceProvider { get; }

    public ConsoleChatSession(
        IServiceProvider serviceProvider,
        Agent agent,
        IOptions<AgentOptions> optionsAccessor,
        ToolContext toolContext,
        IChatOutput? chatOutput = null,
        ILogger<ConsoleChatSession>? logger = null)
    {
        this.ServiceProvider = serviceProvider;
        this._agent = agent;
        this._options = optionsAccessor.Value;
        this.toolContext = toolContext;
        this._logger = logger ?? NullLogger<ConsoleChatSession>.Instance;
        this.output = chatOutput ?? ConsoleOutput.Instance;
        this.commandManager = new CommandManager(CommandRegistry.Commands, this);
    }

    internal void SetAgent(Agent agent)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    public async Task RunAsync(string? initialInput = null)
    {
        using var activity = _activitySource.StartActivity("ConsoleChatSession.RunAsync");
        activity?.SetTag("agent.client_type", this._agent.ClientType);
        activity?.SetTag("agent.model", this._agent.Model);

        var tags = new TagList
        {
            { "agent.client_type", this._agent.ClientType },
            { "agent.model", this._agent.Model },
        };

        _sessionCounter.Add(1, tags);
        var sw = Stopwatch.StartNew();
        bool failed = false;

        int chatTurns = 0;
        ChatSession? chatSession = null;

        this._logger.LogInformation(
            "Starting console chat session. ClientType={ClientType}, Model={Model}",
            this._agent.ClientType, this._agent.Model);

        try
        {
            this.NewMethod();

            using var sessionCts = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                sessionCts.Cancel();
            };

            chatSession = new(this._agent, this._options, this.toolContext);

            // ── REPL loop ─────────────────────────────────────────────────────────────────

            while (true)
            {
                var input = initialInput ?? await ReadLineAsync("[blue]>[/] ", sessionCts.Token);

                if (input is null || sessionCts.IsCancellationRequested)
                {
                    break;
                }

                input = input.Trim();

                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                // Slash commands are handled immediately in the REPL, without going through the agent.
                // This allows for commands that can control the session itself (like /exit or /reset)
                // or perform other actions without needing to be defined as tools.
                if (input.StartsWith('/'))
                {
                    _commandCounter.Add(1, tags);

                    CommandContinuation continuation = await commandManager.ExecuteCommandAsync(input);

                    switch (continuation)
                    {
                        case CommandContinuation.ExitSession:
                        this.output.WriteLine("goldenrod", "Exiting session...");
                        break;
                        case CommandContinuation.Clear:
                        AnsiConsole.Clear();
                        chatSession.Reset();
                        continue;
                        case CommandContinuation.Continue:
                        continue;
                    }

                    break;
                }

                // Snapshot token counts before this run so we can show the per-turn delta.
                long prevIn = chatSession.TotalUsage.InputTokens;
                long prevOut = chatSession.TotalUsage.OutputTokens;

                bool interrupted = false;
                bool streamingStarted = false;
                StringBuilder? streamingBuffer = null;

                try
                {
                    this.output.StartSpinner();

                    // This loops through the events of the turn as they come in from the agent. The main point of complexity here
                    // is handling the fact that TextDeltaEvents may stream in one or more chunks before the AssistantTurnEvent comes in
                    // to indicate the turn is done. We want to buffer those deltas until the turn is done so we can print them together, rather
                    // than interleaving them with any ToolInvokedEvents that may come in during the turn.
                    await foreach (AgentEvent evt in chatSession.SendAsync(input, sessionCts.Token))
                    {
                        switch (evt)
                        {
                            case TextDeltaEvent delta:
                            // This event may come in one or more chunks as the assistant generates a response. We buffer it until the turn
                            // is done (indicated by AssistantTurnEvent) before printing, so that we don't interleave it with ToolInvokedEvents
                            // that may also come in the middle of the response.

                            if (!streamingStarted)
                            {
                                streamingStarted = true;
                                streamingBuffer = new StringBuilder();
                            }

                            streamingBuffer!.Append(delta.Delta);
                            break;

                            case AssistantTurnEvent turn:
                            // This event may come in the middle of streaming text deltas, or as a complete message at the end.
                            // If streaming, we buffer it until the turn is done; if not, we print it immediately.

                            if (streamingStarted)
                            {
                                string buffered = streamingBuffer!.ToString();
                                streamingBuffer = null;
                                streamingStarted = false;
                                this.output.WriteMarkup("[green][[Agent]][/]");
                                MarkdownRenderer.Print(buffered);
                            }
                            else
                            {
                                this.PrintAssistantTurn(turn.Message);
                            }

                            break;

                            case ToolInvokedEvent tool:
                            //  This fires after the agent has actually executed the tool and result is ready.

                            var resultPreview = TruncateString(tool.Result.Content, 100, 3);
                            if (tool.Result.IsError)
                            {
                                this.output.WriteLine("red", resultPreview);
                            }
                            else
                            {
                                //this.output.WriteLine("yellow", $"  ⚙ {tool.ToolName} done.");
                                //this.output.WriteLine("gray", $"    ↳ result: {resultPreview}");
                                this.output.WriteMarkdownInBorderedPanel($"Calling {tool.ToolName}", $"[gray]{resultPreview}[/]");
                            }
                            break;

                            case AgentResultEvent result:
                            // This is the final event of the turn, showing the overall result and token usage for the turn.

                            long deltaIn = chatSession.TotalUsage.InputTokens - prevIn;
                            long deltaOut = chatSession.TotalUsage.OutputTokens - prevOut;
                            this.output.WriteLine("gray",
                                $"  ↳ +{deltaIn:N0} in, +{deltaOut:N0} out  [{result.Status}]");
                            break;

                            // SessionStartedEvent, IterationCompletedEvent: intentionally suppressed.
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    streamingBuffer = null;
                    streamingStarted = false;
                    this.output.WriteLine("goldenrod", "  [interrupted]");
                    interrupted = true;
                }
                finally
                {
                    this.output.StopSpinner();
                }

                if (!interrupted)
                {
                    chatTurns++;
                }

                // If the session CTS was triggered (not just the turn), break the REPL.
                if (sessionCts.IsCancellationRequested)
                {
                    break;
                }

                // Ctrl+C re-armed: sessionCts remains valid, so subsequent turns still work
                // until the user presses Ctrl+C again or types "exit".
            }

            this.output.WriteLine();
            this.output.WriteLine("gray",
                $"Session ended  ·  {chatTurns} turn{(chatTurns == 1 ? "" : "s")}  ·  " +
                $"{chatSession.TotalUsage.InputTokens:N0} in, {chatSession.TotalUsage.OutputTokens:N0} out total");
        }
        catch (Exception ex)
        {
            failed = true;
            _sessionErrorCounter.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            this._logger.LogError(
                ex,
                "Console chat session failed. ClientType={ClientType}, Model={Model}",
                this._agent.ClientType, this._agent.Model);
            throw;
        }
        finally
        {
            sw.Stop();
            _sessionDurationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);
            _sessionTurnCounter.Add(chatTurns, tags);

            if (chatSession?.IsStarted == true)
            {
                _sessionTokenCounter.Add(chatSession.TotalUsage.InputTokens, new TagList
                {
                    { "agent.client_type", this._agent.ClientType },
                    { "agent.model", this._agent.Model },
                    { "agent.token.type", "input" },
                });

                _sessionTokenCounter.Add(chatSession.TotalUsage.OutputTokens, new TagList
                {
                    { "agent.client_type", this._agent.ClientType },
                    { "agent.model", this._agent.Model },
                    { "agent.token.type", "output" },
                });

                activity?.SetTag("agent.usage.input_tokens", chatSession.TotalUsage.InputTokens);
                activity?.SetTag("agent.usage.output_tokens", chatSession.TotalUsage.OutputTokens);
            }

            if (!failed)
            {
                this._logger.LogInformation(
                    "Console chat session completed. Turns={Turns}, DurationMs={DurationMs}",
                    chatTurns, sw.Elapsed.TotalMilliseconds);
            }
        }
    }

    private void NewMethod()
    {
        this.output.WriteLine("cyan", "╔═══════════════════════════════════════════╗");
        this.output.WriteLine("cyan", "║       Agency  ·  Agent Chat Console       ║");
        this.output.WriteLine("cyan", "╚═══════════════════════════════════════════╝");
        this.output.Write(null, "Provider : ");
        this.output.WriteLine("yellow", this._agent.ClientType);
        this.output.Write(null, "Model    : ");
        this.output.WriteLine("yellow", this._agent.Model);
        this.output.WriteLine("gray", "Type /exit to /quit  ·  Ctrl+C to interrupt a turn");
        this.output.WriteLine();
    }

    private async Task<string?> ReadLineAsync(string markup, CancellationToken ct)
    {
        var console = AnsiConsole.Console;

        if (!console.Profile.Capabilities.Interactive)
        {
            return await new TextPrompt<string>(string.Empty)
                .AllowEmpty()
                .ShowAsync(console, ct);
        }

        this.output.WriteLineMarkup(new string('─', System.Console.BufferWidth));
        await Task.Delay(25, ct); // slight delay to ensure the above lines are rendered before we move the cursor
        this.output.WriteLineMarkup(markup);
        await Task.Delay(25, ct); // slight delay to ensure the above lines are rendered before we move the cursor
        this.output.WriteLineMarkup(new string('─', System.Console.BufferWidth));
        await Task.Delay(25, ct); // slight delay to ensure the above lines are rendered before we move the cursor
        var leftMargin = Markup.Remove(markup).Length;
        AnsiConsole.Cursor.MoveUp(2);
        AnsiConsole.Cursor.MoveRight(leftMargin + 1);

        var buffer = new StringBuilder();
        int historyIndex = _history.Count;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                this.output.WriteLine();
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
                    this.output.WriteLine();
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

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    string result = buffer.ToString();
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        _history.Add(result);
                    }
                    AnsiConsole.Cursor.MoveDown(2);
                    return result;

                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Remove(buffer.Length - 1, 1);
                    this.output.Write ("\b \b");
                    break;


                case ConsoleKey.LeftArrow:
                AnsiConsole.Cursor.MoveLeft();
                break;

                case ConsoleKey.Home:
                AnsiConsole.Cursor.SetPosition(leftMargin + 1, System.Console.CursorTop + 1);
                break;

                case ConsoleKey.End:
                AnsiConsole.Cursor.SetPosition(buffer.Length + leftMargin + 1, System.Console.CursorTop + 1);
                break;

                case ConsoleKey.RightArrow:
                    AnsiConsole.Cursor.MoveRight();
                    break;

                case ConsoleKey.Escape:
                    ReplaceBufferLine(buffer, "");
                    buffer.Clear();
                    break;

                case ConsoleKey.UpArrow when historyIndex > 0:
                    historyIndex--;
                    ReplaceBufferLine(buffer, _history[historyIndex]);
                    buffer.Clear();
                    buffer.Append(_history[historyIndex]);
                    break;

                case ConsoleKey.DownArrow:
                    if (historyIndex < _history.Count - 1)
                    {
                        historyIndex++;
                        ReplaceBufferLine(buffer, _history[historyIndex]);
                        buffer.Clear();
                        buffer.Append(_history[historyIndex]);
                    }
                    else
                    {
                        historyIndex = _history.Count;
                        ReplaceBufferLine(buffer, "");
                        buffer.Clear();
                    }

                    break;

                default:
                if (key.KeyChar == '/' && buffer.Length == 0)
                {
                    var commands = CommandRegistry.Commands.Select(cmd => new ConsolePickerRow( cmd.CommandText, cmd.Description )).ToList();
                    this.output.WriteLine();
                    string? picked = ConsolePicker.Show(commands, 0);
                    this.output.WriteMarkup(markup);
                    if (picked is not null)
                    {
                        buffer.Clear();
                        buffer.Append(picked);
                        this.output.Write(picked);
                    }
                }
                else if (key.KeyChar >= 32) // printable character
                {
                    buffer.Append(key.KeyChar);
                    this.output.Write(key.KeyChar.ToString());
                }

                    break;
            }
        }
    }


    private void ReplaceBufferLine(StringBuilder current, string replacement)
    {
        int currentLen = current.Length;
        AnsiConsole.Cursor.MoveLeft(currentLen);
        this.output.Write(replacement);
        int overflow = currentLen - replacement.Length;
        if (overflow > 0)
        {
            this.output.Write(new string(' ', overflow));
            AnsiConsole.Cursor.MoveLeft(overflow);
        }
    }

    private void PrintAssistantTurn(AgentMessage message)
    {
        foreach (ContentBlock block in message.Content)
        {
            switch (block)
            {
                case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                {
                    // This is when Agent wants to write a message
                    this.output.WriteMarkup("[green]●[/]");
                    this.output.WriteLineMarkdown(tb.Text);
                    break;
                }

                case ToolUseBlock tub:

                var command = tub.Input.GetRawText();

                if (command == null)
                {
                    this.output.WriteLine("gray", $"  → calling {tub.Name} with non-text input");
                    break;
                }

                var commandPreview = TruncateString(command, 100, 3);

                // This is when Agent wants to call a tool - we show the tool name and input, but not the output (since it may be large or sensitive)
                this.output.WriteMarkdownInBorderedPanel($"Calling {tub.Name}", $"[gray]{commandPreview}[/]");
                //this.output.WriteLine("magenta", $"  → calling {tub.Name}");
                //this.output.WriteLine("gray", $"    ↳ command: {commandPreview}");
                    break;
            }
        }
    }

    private static string TruncateString(string text, int maxWidth, int maxLines)
    {
        using var tr = new StringReader(text);
        string? line;
        var sb = new StringBuilder();
        int lineCount = 0;
        while ((line = tr.ReadLine()) != null)
        {
            if (lineCount > 0)
            {
                sb.AppendLine();
            }

            sb.Append(
                line.Length > maxWidth
                ? string.Concat(line.AsSpan(0, maxWidth), "...")
                : line);


            if (lineCount++ > maxLines)
            {
                sb.AppendLine("...");
                break;
            }
        }
        return sb.ToString();
    }
}


public class Spinner
{
    private static string[] Frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private Thread? spinnerThread;
    private volatile bool spinnerRunning;

    public void StartSpinner()
    {

        spinnerRunning = true;

        spinnerThread = new Thread(() =>
        {
            
            int frameIndex = 0;

            while (spinnerRunning)
            {
                string frame = Frames[frameIndex % Frames.Length];
                System.Console.Write($"\r{frame} Thinking..  ");
                System.Console.Out.Flush();

                frameIndex++;
                Thread.Sleep(80); // ~12.5 FPS
            }
        })
        {
            IsBackground = true,
            Name = "ConsoleSpinner"
        };

        spinnerThread.Start();
    }

    public void StopSpinner()
    {
        spinnerRunning = false;
        spinnerThread?.Join(timeout: TimeSpan.FromMilliseconds(500));
        spinnerThread = null;
        System.Console.Write("\r" + new string(' ', 20) + "\r"); // Clear the line
    }
}