using Agency.Agentic;
using Agency.Llm.Claude;
using Agency.Llm.Common;
using Agency.Llm.Common.Messages;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

// ── Configuration ─────────────────────────────────────────────────────────────

var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var provider = (configuration["Agent:Provider"] ?? "OpenAI").Trim();
var section = $"Agent:{provider}";

var model = configuration[$"{section}:Model"]
    ?? throw new InvalidOperationException(
        $"Missing required configuration value '{section}:Model'.");

// ── Build LLM client ──────────────────────────────────────────────────────────

ILlmClient llmClient = provider.ToUpperInvariant() switch
{
    "CLAUDE" => new ClaudeClient(Options.Create(new ClaudeClientOptions
    {
        ApiKey = configuration[$"{section}:ApiKey"] ?? string.Empty,
        BaseUrl = configuration[$"{section}:BaseUrl"],
    })),
    _ => new OpenAIClient(Options.Create(new OpenAIClientOptions
    {
        ApiKey = configuration[$"{section}:ApiKey"] ?? "lm-studio",
        BaseUrl = configuration[$"{section}:BaseUrl"],
    })),
};

var agent = new Agent(llmClient, model, stream: false);

// ── Welcome banner ────────────────────────────────────────────────────────────

Out(ConsoleColor.Cyan,  "╔═══════════════════════════════════════════╗");
Out(ConsoleColor.Cyan,  "║       Agency  ·  Agent Chat Console        ║");
Out(ConsoleColor.Cyan,  "╚═══════════════════════════════════════════╝");
OutInline(null,            "Provider : ");
Out(ConsoleColor.Yellow,   provider);
OutInline(null,            "Model    : ");
Out(ConsoleColor.Yellow,   model);
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
    OutInline(ConsoleColor.Cyan, "> ");
    var input = Console.ReadLine();

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
    bool interrupted = false;

    try
    {
        await foreach (AgentEvent evt in agent.RunAsync(ctx, turnCts.Token))
        {
            switch (evt)
            {
                case AssistantTurnEvent turn:
                    PrintAssistantTurn(turn.Message);
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
                    long deltaIn  = ctx.TotalUsage.InputTokens  - prevIn;
                    long deltaOut = ctx.TotalUsage.OutputTokens - prevOut;
                    Out(ConsoleColor.DarkGray,
                        $"  ↳ +{deltaIn:N0} in, +{deltaOut:N0} out  [{result.Status}]");
                    break;

                // SessionStartedEvent, IterationCompletedEvent, TextDeltaEvent:
                // intentionally suppressed for a clean chat UX.
            }
        }
    }
    catch (OperationCanceledException)
    {
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

// ── Helpers ───────────────────────────────────────────────────────────────────

static void PrintAssistantTurn(AgentMessage message)
{
    foreach (ContentBlock block in message.Content)
    {
        switch (block)
        {
            case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                OutInline(ConsoleColor.Green, "[Agent] ");
                Console.WriteLine(tb.Text);
                break;

            case ToolUseBlock tub:
                Out(ConsoleColor.Magenta, $"  → calling {tub.Name}");
                break;
        }
    }
}

static void Out(ConsoleColor? color, string text)
{
    if (color.HasValue)
    {
        Console.ForegroundColor = color.Value;
    }

    Console.WriteLine(text);
    Console.ResetColor();
}

static void OutInline(ConsoleColor? color, string text)
{
    if (color.HasValue)
    {
        Console.ForegroundColor = color.Value;
    }

    Console.Write(text);
    Console.ResetColor();
}
