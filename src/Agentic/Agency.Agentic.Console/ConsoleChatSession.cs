namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Agentic.Console.Commands;
using Agency.Agentic.Contexts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

internal sealed class ConsoleChatSession
{
    private const string PromptMarkup= "[blue]❯ [/]";
    private const string AssistantMarkup = "[green]● [/]";

    public const string ActivitySourceName = "Agency.Agentic.Console";
    public const string MeterName = "Agency.Agentic.Console";

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
    private readonly ConsoleInputReader _inputReader;
    private readonly AgentOptions _options;
    private readonly ILogger<ConsoleChatSession> _logger;
    private readonly ToolContext toolContext;
    private Agent _agent;
    private ChatSession? _chatSession;

    internal IServiceProvider ServiceProvider { get; }

    public ConsoleChatSession(
        IServiceProvider serviceProvider,
        Agent agent,
        IOptions<AgentOptions> optionsAccessor,
        ToolContext toolContext,
        IChatOutput chatOutput,
        ILogger<ConsoleChatSession>? logger = null)
    {
        this.ServiceProvider = serviceProvider;
        this._agent = agent;
        this._options = optionsAccessor.Value;
        this.toolContext = toolContext;
        this._logger = logger ?? NullLogger<ConsoleChatSession>.Instance;
        this.output = chatOutput;
        this._inputReader = new ConsoleInputReader(this.output);
        this.commandManager = new CommandManager(CommandRegistry.Commands, this);
    }

    internal void SetAgent(Agent agent)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this._chatSession?.SetAgent(agent);
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

        this._logger.LogInformation(
            "Starting console chat session. ClientType={ClientType}, Model={Model}",
            this._agent.ClientType, this._agent.Model);

        try
        {
            this.WriteHeader();

            using var sessionCts = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                sessionCts.Cancel();
            };

            this._chatSession = new(this._agent, this._options, this.toolContext);

            // ── REPL loop ─────────────────────────────────────────────────────────────────

            while (true)
            {
                var input = initialInput ?? await this._inputReader.ReadLineAsync(PromptMarkup, sessionCts.Token);

                if (input is null || sessionCts.IsCancellationRequested)
                {
                    break;
                }

                input = input.Trim();

                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)
                    || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    this.output.WriteLine("yellow", "Exiting session...");
                    break;
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
                        this.output.WriteLine("yellow", "Exiting session...");
                        break;
                        case CommandContinuation.Clear:
                        AnsiConsole.Clear();
                        this._chatSession.Reset();
                        continue;
                        case CommandContinuation.Continue:
                        continue;
                    }

                    break;
                }

                // Snapshot token counts before this run so we can show the per-turn delta.
                long prevIn = this._chatSession.TotalUsage.InputTokens;
                long prevOut = this._chatSession.TotalUsage.OutputTokens;

                bool interrupted = false;

                try
                {
                    this.output.StartSpinner();

                    await foreach (AgentEvent evt in this._chatSession.SendAsync(input, sessionCts.Token))
                    {
                        switch (evt)
                        {
                            case AssistantTurnEvent turn:
                            this.PrintAssistantTurn(turn.Message);
                            this.output.StopSpinner();
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
                                this.output.WriteMarkdownInBorderedPanel($"Calling {tool.ToolName}", $"[gray]{resultPreview}[/]");
                            }
                            break;

                            case AgentResultEvent result:
                            // This is the final event of the turn, showing the overall result and token usage for the turn.

                            long deltaIn = this._chatSession.TotalUsage.InputTokens - prevIn;
                            long deltaOut = this._chatSession.TotalUsage.OutputTokens - prevOut;
                            this.output.WriteLine("gray",
                                $"  ↳ +{deltaIn:N0} in, +{deltaOut:N0} out  [{result.Status}]");
                            if (result.Status == AgentResultStatus.Error && result.FinalText is { } errorText)
                            {
                                this.output.WriteLine("red", $"  {errorText}");
                            }
                            break;

                            // SessionStartedEvent, IterationCompletedEvent: intentionally suppressed.
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    this.output.WriteLine("yellow", "  [interrupted]");
                    interrupted = true;
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
                $"{this._chatSession.TotalUsage.InputTokens:N0} in, {this._chatSession.TotalUsage.OutputTokens:N0} out total");
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

            if (this._chatSession?.IsStarted == true)
            {
                _sessionTokenCounter.Add(this._chatSession.TotalUsage.InputTokens, new TagList
                {
                    { "agent.client_type", this._agent.ClientType },
                    { "agent.model", this._agent.Model },
                    { "agent.token.type", "input" },
                });

                _sessionTokenCounter.Add(this._chatSession.TotalUsage.OutputTokens, new TagList
                {
                    { "agent.client_type", this._agent.ClientType },
                    { "agent.model", this._agent.Model },
                    { "agent.token.type", "output" },
                });

                activity?.SetTag("agent.usage.input_tokens", this._chatSession.TotalUsage.InputTokens);
                activity?.SetTag("agent.usage.output_tokens", this._chatSession.TotalUsage.OutputTokens);
            }

            if (!failed)
            {
                this._logger.LogInformation(
                    "Console chat session completed. Turns={Turns}, DurationMs={DurationMs}",
                    chatTurns, sw.Elapsed.TotalMilliseconds);
            }
        }
    }

    private void PrintAssistantTurn(ChatMessage message)
    {
        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                    this.output.WriteMarkup(AssistantMarkup);
                    this.output.WriteLineMarkdown(tc.Text);
                    break;

                case FunctionCallContent fcc:
                    var argsPreview = TruncateString(
                        System.Text.Json.JsonSerializer.Serialize(fcc.Arguments),
                        100,
                        3);
                    this.output.WriteMarkdownInBorderedPanel($"Calling {fcc.Name}", $"[gray]{argsPreview}[/]");
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

    private void WriteHeader()
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
}

