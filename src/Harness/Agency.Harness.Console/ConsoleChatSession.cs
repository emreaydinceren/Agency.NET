using Agency.Harness.Console.Commands;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spectre.Console;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace Agency.Harness.Console;
internal sealed class ConsoleChatSession
{
    private const string PromptMarkup= "[blue]❯ [/]";
    private const string AssistantMarkup = "[green]● [/]";

    public const string ActivitySourceName = "Agency.Harness.Console";
    public const string MeterName = "Agency.Harness.Console";

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

            this._chatSession = new(this._agent, this._options, this.toolContext,
                new UserSpecificContext { Id = this._options.UserId ?? System.Environment.UserName });

            bool shouldExitSession = false;
            CancellationTokenSource? turnCts = null;

            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                if (turnCts is { IsCancellationRequested: false })
                {
                    turnCts.Cancel();
                }
                else
                {
                    shouldExitSession = true;
                }
            };

            // ── REPL loop ─────────────────────────────────────────────────────────────────

            while (true)
            {
                if (shouldExitSession)
                {
                    break;
                }

                turnCts = new CancellationTokenSource();
                var input = initialInput ?? await this._inputReader.ReadLineAsync(PromptMarkup, turnCts.Token);

                if (input is null)
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
                        if (this._chatSession is not null)
                        {
                            await this._chatSession.DisposeAsync();
                            this._chatSession = null;
                        }
                        this._chatSession = new(this._agent, this._options, this.toolContext,
                            new UserSpecificContext { Id = this._options.UserId ?? System.Environment.UserName });
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
                TimeSpan lastLlmDuration = TimeSpan.Zero;

                try
                {
                    this.output.StartSpinner();

                    // Park loop: process the stream, and if the turn parks for permission,
                    // gather answers and resume — repeating until the turn is no longer parked
                    // or the user cancels the permission picker.
                    var pendingRequests = new List<PermissionRequestedEvent>();
                    (bool parked, lastLlmDuration) = await this.ProcessStreamAsync(
                        this._chatSession.SendAsync(input, turnCts.Token),
                        pendingRequests,
                        prevIn, prevOut,
                        lastLlmDuration,
                        turnCts.Token);

                    while (parked)
                    {
                        var responses = this.CollectPermissionResponses(pendingRequests);
                        if (responses is null)
                        {
                            // User cancelled the picker (Escape) — abandon: the next SendAsync
                            // will auto-deny all pending calls via the abandonment path (§6.4).
                            break;
                        }

                        pendingRequests.Clear();
                        (parked, lastLlmDuration) = await this.ProcessStreamAsync(
                            this._chatSession.ResumeWithPermissionsAsync(responses, turnCts.Token),
                            pendingRequests,
                            prevIn, prevOut,
                            lastLlmDuration,
                            turnCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    this.output.StopSpinner();
                    this.output.WriteLine("yellow", "  [interrupted]");
                    interrupted = true;
                }

                if (!interrupted)
                {
                    chatTurns++;
                }

                turnCts.Dispose();
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
            // Fire OnSessionEnd → SessionDisposed distillation before metrics are recorded.
            if (this._chatSession is not null)
            {
                await this._chatSession.DisposeAsync();
            }

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

    /// <summary>
    /// Drains <paramref name="stream"/> through the standard rendering switch.
    /// Returns <c>(Parked: true, …)</c> when the stream ends with
    /// <see cref="AgentResultStatus.AwaitingPermission"/> (turn parked);
    /// <c>(Parked: false, …)</c> for any other terminal status.
    /// The returned <c>LastLlmDuration</c> reflects any <see cref="IterationCompletedEvent"/>
    /// observed in this stream segment, or <paramref name="priorLlmDuration"/> when none was seen.
    /// Collected <see cref="PermissionRequestedEvent"/>s are appended to
    /// <paramref name="pendingRequests"/> (caller clears between park cycles).
    /// </summary>
    private async Task<(bool Parked, TimeSpan LastLlmDuration)> ProcessStreamAsync(
        IAsyncEnumerable<AgentEvent> stream,
        List<PermissionRequestedEvent> pendingRequests,
        long prevIn,
        long prevOut,
        TimeSpan priorLlmDuration,
        CancellationToken ct)
    {
        bool parked = false;
        TimeSpan lastLlmDuration = priorLlmDuration;

        await foreach (AgentEvent evt in stream.WithCancellation(ct))
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

                case PermissionRequestedEvent permReq:
                pendingRequests.Add(permReq);
                break;

                case IterationCompletedEvent iteration:
                lastLlmDuration = iteration.LlmDuration;
                break;

                case AgentResultEvent result:
                // This is the final event of the turn, showing the overall result and token usage for the turn.

                if (result.Status == AgentResultStatus.AwaitingPermission)
                {
                    // The turn has parked. The spinner has not been stopped yet (no AssistantTurnEvent
                    // was emitted before parking), so stop it now before rendering permission panels.
                    this.output.StopSpinner();
                    parked = true;
                    break;
                }

                long deltaIn = this._chatSession!.TotalUsage.InputTokens - prevIn;
                long deltaOut = this._chatSession!.TotalUsage.OutputTokens - prevOut;
                long totalDelta = deltaIn + deltaOut;

                string throughput = lastLlmDuration.TotalSeconds > 0
                    ? $"  {(totalDelta / lastLlmDuration.TotalSeconds):F1} tok/s"
                    : string.Empty;

                this.output.WriteLine("gray",
                    $"  ↳ +{deltaIn:N0} in, +{deltaOut:N0} out{throughput}  [{result.Status}]");
                if (result.Status == AgentResultStatus.Error && result.FinalText is { } errorText)
                {
                    this.output.WriteLine("red", $"  {errorText}");
                }
                break;

                // SessionStartedEvent: intentionally suppressed.
            }
        }

        return (parked, lastLlmDuration);
    }

    /// <summary>
    /// Renders a permission panel and picker for each pending request, then returns
    /// the collected <see cref="PermissionResponse"/> list.  Returns
    /// <see langword="null"/> if the user cancels (Escape) on any picker — the caller
    /// must not resume in that case and should fall back to the REPL; the next
    /// <see cref="ChatSession.SendAsync"/> call will auto-abandon the parked turn.
    /// </summary>
    private IReadOnlyList<PermissionResponse>? CollectPermissionResponses(
        List<PermissionRequestedEvent> pending)
    {
        var responses = new List<PermissionResponse>(pending.Count);

        foreach (PermissionRequestedEvent req in pending)
        {
            // ── Panel content ────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine($"Tool: [bold]{Markup.Escape(req.ToolName)}[/]");

            if (req.KeyValue is not null)
            {
                sb.AppendLine($"Input: [gray]{Markup.Escape(req.KeyValue)}[/]");
            }
            else
            {
                // Truncate raw JSON to ~200 chars for readability.
                string rawJson = req.Input.GetRawText();
                if (rawJson.Length > 200)
                {
                    rawJson = string.Concat(rawJson.AsSpan(0, 200), "…");
                }
                sb.AppendLine($"Input: [gray]{Markup.Escape(rawJson)}[/]");
            }

            sb.AppendLine($"Proposed rule: [yellow]{Markup.Escape(req.ProposedRule)}[/]");

            if (req.Source == PermissionRequestSource.Hook && req.Reason is not null)
            {
                sb.AppendLine($"Hook reason: [orange3]{Markup.Escape(req.Reason)}[/]");
            }

            this.output.WriteMarkdownInBorderedPanel("Permission required", sb.ToString().TrimEnd());

            // ── Picker rows ──────────────────────────────────────────────────
            // §3.5: hide "Allow always" for Hook-sourced requests because persisted allow
            // rules cannot suppress a recurring hook Ask.
            var rows = req.Source == PermissionRequestSource.Hook
                ? new List<ConsolePickerRow>
                {
                    new("Allow once",  "AllowOnce"),
                    new("Deny once",   "DenyOnce"),
                    new("Deny always", "DenyAlways"),
                }
                : new List<ConsolePickerRow>
                {
                    new("Allow once",  "AllowOnce"),
                    new("Allow always", "AllowAlways"),
                    new("Deny once",   "DenyOnce"),
                    new("Deny always", "DenyAlways"),
                };

            // returnItemIndex = 1 returns the second column (the internal kind key).
            string? picked = ConsolePicker.Show(rows, returnItemIndex: 1, title: "Choose an action:");

            if (picked is null)
            {
                // User pressed Escape / cancelled — signal caller to abandon.
                return null;
            }

            PermissionResponseKind kind = picked switch
            {
                "AllowOnce"   => PermissionResponseKind.AllowOnce,
                "AllowAlways" => PermissionResponseKind.AllowAlways,
                "DenyOnce"    => PermissionResponseKind.DenyOnce,
                "DenyAlways"  => PermissionResponseKind.DenyAlways,
                _             => PermissionResponseKind.DenyOnce,
            };

            string? message = null;
            if (kind is PermissionResponseKind.DenyOnce or PermissionResponseKind.DenyAlways)
            {
                this.output.Write("gray", "  Reason for the model (Enter to skip): ");
                message = System.Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(message))
                {
                    message = null;
                }
            }

            responses.Add(new PermissionResponse(req.RequestId, kind, message));
        }

        return responses;
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

