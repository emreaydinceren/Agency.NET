using Agency.Agentic;
using Agency.Llm.Claude;
using Agency.Llm.Common;
using Agency.Llm.Common.Messages;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NTokenizers.Extensions.Spectre.Console;
using NTokenizers.Extensions.Spectre.Console.Styles;
using Spectre.Console;
using System.Text;

internal class Program
{
    public static async Task Main()
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AgentOptions>()
            .BindConfiguration("Agent");

        services.AddSingleton<ILlmClient>(sp =>
        {
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

            var providerOptions = options.GetSelectedProviderOptions();

            return options.Provider.ToUpperInvariant() switch
            {
                "CLAUDE" => new ClaudeClient(providerOptions),
                "OPENAI" => new OpenAIClient(providerOptions),
                _ => throw new InvalidOperationException($"Unsupported provider '{options.Provider}'."),
            };
        });

        services.AddSingleton(sp =>
        {
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            string model = options.Model
                ?? throw new InvalidOperationException(
                    "Missing required configuration value 'Agent:Model'.");
            ILlmClient llmClient = sp.GetRequiredService<ILlmClient>();
            return new Agent(llmClient, model, stream: options.Stream);
        });

        services.AddSingleton<ConsoleChatSession>();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var app = serviceProvider.GetRequiredService<ConsoleChatSession>();
        await app.RunAsync();
    }
}

internal sealed class ConsoleChatSession(Agent agent, IOptions<AgentOptions> optionsAccessor)
{
    private readonly Agent _agent = agent;
    private readonly AgentOptions _options = optionsAccessor.Value;

    public async Task RunAsync()
    {
        string provider = _options.Provider;
        string model = _options.Model
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
        Out(ConsoleColor.DarkGray, "Type \"exit\" to quit  ·  Ctrl+C to interrupt a turn");
        Console.WriteLine();

        // ── Session state ─────────────────────────────────────────────────────────────

        using var sessionCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
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
            var input = Console.IsInputRedirected
                ? Console.ReadLine()
                : AnsiConsole.Ask<string>(">");

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

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            Console.WriteLine();

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

            Console.WriteLine();

            // If the session CTS was triggered (not just the turn), break the REPL.
            if (sessionCts.IsCancellationRequested)
            {
                break;
            }

            // Ctrl+C re-armed: sessionCts remains valid, so subsequent turns still work
            // until the user presses Ctrl+C again or types "exit".
        }

        // ── Session summary ───────────────────────────────────────────────────────────

        Console.WriteLine();
        Out(ConsoleColor.DarkGray,
            $"Session ended  ·  {chatTurns} turn{(chatTurns == 1 ? "" : "s")}  ·  " +
            $"{ctx?.TotalUsage.InputTokens ?? 0:N0} in, {ctx?.TotalUsage.OutputTokens ?? 0:N0} out total");
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
            Console.ForegroundColor = color.Value;
        }

        Console.WriteLine(text);
        Console.ResetColor();
    }

    private static void OutInline(ConsoleColor? color, string text)
    {
        if (color.HasValue)
        {
            Console.ForegroundColor = color.Value;
        }

        Console.Write(text);
        Console.ResetColor();
    }
}
