namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Agentic.Console.Commands;
using Agency.Llm.Common.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NTokenizers.Extensions.Spectre.Console;
using NTokenizers.Extensions.Spectre.Console.Styles;
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
    private readonly AgentOptions _options;
    private readonly List<string> _history = [];
    private readonly ILogger<ConsoleChatSession> _logger;
    private Agent _agent;
    internal IServiceProvider ServiceProvider { get; }

    public ConsoleChatSession(
        IServiceProvider serviceProvider,
        Agent agent,
        IOptions<AgentOptions> optionsAccessor,
        ILogger<ConsoleChatSession>? logger = null)
    {
        this.ServiceProvider = serviceProvider;
        this._agent = agent;
        this._options = optionsAccessor.Value;
        this._logger = logger ?? NullLogger<ConsoleChatSession>.Instance;
        this.commandManager = new CommandManager(CommandRegistery.Commands, this);
    }

    public async Task RunAsync()
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

        Context? ctx = null;
        int chatTurns = 0;

        this._logger.LogInformation(
            "Starting console chat session. ClientType={ClientType}, Model={Model}",
            this._agent.ClientType, this._agent.Model);

        try
        {
            Out(ConsoleColor.Cyan, "╔═══════════════════════════════════════════╗");
            Out(ConsoleColor.Cyan, "║       Agency  ·  Agent Chat Console       ║");
            Out(ConsoleColor.Cyan, "╚═══════════════════════════════════════════╝");
            OutInline(null, "Provider : ");
            Out(ConsoleColor.Yellow, this._agent.ClientType);
            OutInline(null, "Model    : ");
            Out(ConsoleColor.Yellow, this._agent.Model);
            Out(ConsoleColor.DarkGray, "Type /exit to /quit  ·  Ctrl+C to interrupt a turn");
            System.Console.WriteLine();

            using var sessionCts = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                sessionCts.Cancel();
            };

            // ── REPL loop ─────────────────────────────────────────────────────────────────

            while (true)
            {
                var input = await ReadLineAsync(sessionCts.Token);

                if (input is null || sessionCts.IsCancellationRequested)
                {
                    break;
                }

                input = input.Trim();

                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                if (input.StartsWith('/'))
                {
                    _commandCounter.Add(1, tags);
                    var continuation = await commandManager.ExecuteCommandAsync(input);

                    switch (continuation)
                    {
                        case CommandContinuation.ExitSession:
                            Out(ConsoleColor.DarkYellow, "Exiting session...");
                            break;
                        case CommandContinuation.Continue:
                            continue;
                    }

                    break;
                }

                System.Console.WriteLine();

                // Snapshot token counts before this run so we can show the per-turn delta.
                long prevIn = ctx?.TotalUsage.InputTokens ?? 0;
                long prevOut = ctx?.TotalUsage.OutputTokens ?? 0;

                ctx ??= Agent.CreateContext(input);

                bool interrupted = false;
                bool streamingStarted = false;
                StringBuilder? streamingBuffer = null;

                try
                {
                    await foreach (AgentEvent evt in _agent.ChatAsync(input, ctx, _options, sessionCts.Token))
                    {
                        switch (evt)
                        {
                            case TextDeltaEvent delta:
                                if (!streamingStarted)
                                {
                                    streamingStarted = true;
                                    streamingBuffer = new StringBuilder();
                                }

                                streamingBuffer!.Append(delta.Delta);
                                break;

                            case AssistantTurnEvent turn:
                                if (streamingStarted)
                                {
                                    string buffered = streamingBuffer!.ToString();
                                    streamingBuffer = null;
                                    streamingStarted = false;
                                    AnsiConsole.Markup("[green][[Agent]][/] ");
                                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(buffered));
                                    await AnsiConsole.Console.WriteMarkdownAsync(ms, MarkdownStyles.Default, Encoding.UTF8, sessionCts.Token);
                                }
                                else
                                {
                                    await PrintAssistantTurnAsync(turn.Message, sessionCts.Token);
                                }

                                break;

                            case ToolInvokedEvent tool:
                                OutInline(ConsoleColor.Yellow, $"  ⚙ {tool.ToolName}");
                                OutInline(ConsoleColor.DarkGray, " → ");
                                var resultPreview = tool.Result.Content.Length > 100
                                    ? string.Concat(tool.Result.Content.AsSpan(0, 100), "…")
                                    : tool.Result.Content;
                                Out(tool.Result.IsError ? ConsoleColor.Red : ConsoleColor.DarkGreen, resultPreview);
                                break;

                            case AgentResultEvent result:
                                long deltaIn = ctx.TotalUsage.InputTokens - prevIn;
                                long deltaOut = ctx.TotalUsage.OutputTokens - prevOut;
                                Out(ConsoleColor.DarkGray,
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
                    Out(ConsoleColor.DarkYellow, "  [interrupted]");
                    interrupted = true;
                }

                if (!interrupted)
                {
                    chatTurns++;
                }

                System.Console.WriteLine();

                // If the session CTS was triggered (not just the turn), break the REPL.
                if (sessionCts.IsCancellationRequested)
                {
                    break;
                }

                // Ctrl+C re-armed: sessionCts remains valid, so subsequent turns still work
                // until the user presses Ctrl+C again or types "exit".
            }

            System.Console.WriteLine();
            Out(ConsoleColor.DarkGray,
                $"Session ended  ·  {chatTurns} turn{(chatTurns == 1 ? "" : "s")}  ·  " +
                $"{ctx?.TotalUsage.InputTokens ?? 0:N0} in, {ctx?.TotalUsage.OutputTokens ?? 0:N0} out total");
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

            if (ctx is not null)
            {
                _sessionTokenCounter.Add(ctx.TotalUsage.InputTokens, new TagList
                {
                    { "agent.client_type", this._agent.ClientType },
                    { "agent.model", this._agent.Model },
                    { "agent.token.type", "input" },
                });

                _sessionTokenCounter.Add(ctx.TotalUsage.OutputTokens, new TagList
                {
                    { "agent.client_type", this._agent.ClientType },
                    { "agent.model", this._agent.Model },
                    { "agent.token.type", "output" },
                });

                activity?.SetTag("agent.usage.input_tokens", ctx.TotalUsage.InputTokens);
                activity?.SetTag("agent.usage.output_tokens", ctx.TotalUsage.OutputTokens);
            }

            if (!failed)
            {
                this._logger.LogInformation(
                    "Console chat session completed. Turns={Turns}, DurationMs={DurationMs}",
                    chatTurns, sw.Elapsed.TotalMilliseconds);
            }
        }
    }

    public void SetAgent(Agent agent)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (System.Console.IsInputRedirected)
        {
            return System.Console.ReadLine();
        }

        System.Console.Write("> ");

        var buffer = new StringBuilder();
        int historyIndex = _history.Count;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                System.Console.WriteLine();
                return null;
            }

            if (!System.Console.KeyAvailable)
            {
                try
                {
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException)
                {
                    System.Console.WriteLine();
                    return null;
                }

                continue;
            }

            ConsoleKeyInfo key = System.Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    System.Console.WriteLine();
                    string result = buffer.ToString();
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        _history.Add(result);
                    }

                    return result;

                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Remove(buffer.Length - 1, 1);
                    System.Console.Write("\b \b");
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
                    var (startLeft, starTop) = (System.Console.CursorLeft, System.Console.CursorTop);
                    var commands = CommandRegistery.Commands.Select(cmd => new ConsolePickerRow( cmd.CommandText, cmd.Description )).ToList();
                    System.Console.WriteLine();
                    string? picked = ConsolePicker.Show(commands, 0);
                    System.Console.CursorTop = starTop;
                    System.Console.CursorLeft = startLeft;
                    if (picked is null)
                    {
                        System.Console.Write(buffer);
                    }
                    if (picked is not null)
                    {
                        ReplaceBufferLine(buffer, picked);
                        buffer.Clear();
                        buffer.Append(picked);
                    }
                }
                else if (key.KeyChar >= 32) // printable character
                {
                    buffer.Append(key.KeyChar);
                    System.Console.Write(key.KeyChar);
                }

                    break;
            }
        }
    }


    private static void ReplaceBufferLine(StringBuilder current, string replacement)
    {
        int currentLen = current.Length;
        System.Console.Write(new string('\b', currentLen));
        System.Console.Write(replacement);
        int overflow = currentLen - replacement.Length;
        if (overflow > 0)
        {
            System.Console.Write(new string(' ', overflow));
            System.Console.Write(new string('\b', overflow));
        }
    }

    private static async Task PrintAssistantTurnAsync(AgentMessage message, CancellationToken ct)
    {
        foreach (ContentBlock block in message.Content)
        {
            switch (block)
            {
                case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                {
                    AnsiConsole.Markup("[green][[Agent]][/] ");
                    byte[] textBytes = Encoding.UTF8.GetBytes(tb.Text);
                    using var ms = new MemoryStream(textBytes);
                    await AnsiConsole.Console.WriteMarkdownAsync(ms, MarkdownStyles.Default, Encoding.UTF8, ct);
                    break;
                }

                case ToolUseBlock tub:
                    Out(ConsoleColor.Magenta, $"  → calling {tub.Name}");
                    break;
            }
        }
    }

    private static void Out(ConsoleColor? color, string text)
    {
        if (color.HasValue)
        {
            System.Console.ForegroundColor = color.Value;
        }

        System.Console.WriteLine(text);
        System.Console.ResetColor();
    }

    private static void OutInline(ConsoleColor? color, string text)
    {
        if (color.HasValue)
        {
            System.Console.ForegroundColor = color.Value;
        }

        System.Console.Write(text);
        System.Console.ResetColor();
    }
}
