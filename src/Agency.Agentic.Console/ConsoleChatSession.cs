
namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Agentic.Console.Commands;
using Agency.Llm.Common.Messages;
using Microsoft.Extensions.Options;
using NTokenizers.Extensions.Spectre.Console;
using NTokenizers.Extensions.Spectre.Console.Styles;
using Spectre.Console;
using System.Text;

internal sealed class ConsoleChatSession(Agent agent, IOptions<AgentOptions> optionsAccessor)
{
    private static CommandManager commandManager = new(CommandRegistery.Commands);
    private readonly Agent _agent = agent;
    private readonly AgentOptions _options = optionsAccessor.Value;
    private readonly List<string> _history = [];


    public async Task RunAsync()
    {
        string provider = _options.DefaultClientName;
        string model = _options.DefaultModel
            ?? throw new InvalidOperationException("Missing required configuration value 'Agent:Model'.");
        int? turnTimeoutSeconds = _options.TurnTimeoutSeconds;

        // ── Welcome banner ────────────────────────────────────────────────────────────

        Out(ConsoleColor.Cyan, "╔═══════════════════════════════════════════╗");
        Out(ConsoleColor.Cyan, "║       Agency  ·  Agent Chat Console       ║");
        Out(ConsoleColor.Cyan, "╚═══════════════════════════════════════════╝");
        OutInline(null, "Provider : ");
        Out(ConsoleColor.Yellow, provider);
        OutInline(null, "Model    : ");
        Out(ConsoleColor.Yellow, model);
        Out(ConsoleColor.DarkGray, "Type /exit to /quit  ·  Ctrl+C to interrupt a turn");
        System.Console.WriteLine();

        // ── Session state ─────────────────────────────────────────────────────────────

        using var sessionCts = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            // Prevent the process from terminating; cancel the current agent turn instead.
            e.Cancel = true;
            sessionCts.Cancel();
        };

        Context? ctx = null;
        int chatTurns = 0;

        // ── REPL loop ─────────────────────────────────────────────────────────────────

        while (true)
        {
            var input = await ReadLineAsync(sessionCts.Token);

            if (input is null)
            {
                break; // EOF (piped input exhausted)
            }

            if (sessionCts.IsCancellationRequested)
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
                var continuation = commandManager.ExecuteCommand(input);

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
            long prevIn  = ctx?.TotalUsage.InputTokens  ?? 0;
            long prevOut = ctx?.TotalUsage.OutputTokens ?? 0;

            if (ctx is null)
            {
                // First user turn — agent seeds the conversation from Query.Prompt.
                ctx = new Context
                {
                    Query = new QueryContext { Prompt = input },
                    Temporal = new TemporalContext { CurrentDateUtc = DateTimeOffset.UtcNow },
                };
            }
            else
            {
                // Subsequent turns — append the new user message before calling RunAsync.
                // Agent.RunAsync only seeds when the conversation is empty, so reusing the
                // same Context gives a persistent multi-turn conversation within one session.
                ctx.Conversation.Append(new AgentMessage(MessageRole.User, [new TextBlock(input)]));
            }

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
            if (turnTimeoutSeconds is > 0)
            {
                turnCts.CancelAfter(TimeSpan.FromSeconds(turnTimeoutSeconds.Value));
            }

            bool interrupted = false;
            bool streamingStarted = false;
            StringBuilder? streamingBuffer = null;

            try
            {
                await foreach (AgentEvent evt in _agent.RunAsync(ctx, turnCts.Token))
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
                                await AnsiConsole.Console.WriteMarkdownAsync(ms, MarkdownStyles.Default, Encoding.UTF8, turnCts.Token);
                            }
                            else
                            {
                                await PrintAssistantTurnAsync(turn.Message, turnCts.Token);
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

        // ── Session summary ───────────────────────────────────────────────────────────

        System.Console.WriteLine();
        Out(ConsoleColor.DarkGray,
            $"Session ended  ·  {chatTurns} turn{(chatTurns == 1 ? "" : "s")}  ·  " +
            $"{ctx?.TotalUsage.InputTokens ?? 0:N0} in, {ctx?.TotalUsage.OutputTokens ?? 0:N0} out total");
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
                        var commands = CommandRegistery.Commands.Select(cmd => new[] { cmd.CommandText, cmd.Description }).ToArray();

                        buffer.Append('/');
                        System.Console.Write('/');
                        int pickerTop  = System.Console.CursorTop;
                        int pickerLeft = System.Console.CursorLeft;
                        string? picked = ConsolePicker.Show(commands, 0, pickerTop, pickerLeft);
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
