using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using Agency.Harness.Test.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agency.Harness.Test;

/// <summary>
/// Verifies tool-call logging: calls and failures are always logged by name, while the verbose
/// input/error payloads are gated behind <see cref="AgentOptions.LogToolPayloads"/>.
/// </summary>
public sealed class AgentToolLoggingTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ChatResponse ToolCallWithArgs(string id, string name, object args) =>
        new([new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(id, name,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)))])])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]) { FinishReason = ChatFinishReason.Stop };

    /// <summary>Runs one tool-calling turn against a tool that returns an error result.</summary>
    private static async Task<CapturingLogger<Agent>> RunFailingToolTurn(bool logToolPayloads)
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallWithArgs("c1", "failing_tool", new { secret = "s3cr3t" }));
        llm.EnqueueResponse(TextResponse("done"));

        var registry = new ToolRegistry([
            new FakeTool("failing_tool", _ => new ToolResult("boom-error-detail", IsError: true))]);

        var logger = new CapturingLogger<Agent>();
        var agent = new Agent(llm, "model", logger: logger, logToolPayloads: logToolPayloads);
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "go" },
            Tools = new ToolContext { Registry = registry },
        };

        await foreach (var _ in agent.RunAsync(ctx, CancellationToken.None)) { }
        return logger;
    }

    [Fact]
    public async Task ToolCallAndFailure_AreAlwaysLoggedByName()
    {
        CapturingLogger<Agent> logger = await RunFailingToolTurn(logToolPayloads: false);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Invoking tool failing_tool"));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("failing_tool")
            && e.Message.Contains("returned an error result"));
    }

    [Fact]
    public async Task PayloadsRedacted_WhenLoggingDisabled()
    {
        CapturingLogger<Agent> logger = await RunFailingToolTurn(logToolPayloads: false);

        // Neither the tool input nor the error content leak into the logs.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("s3cr3t"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("boom-error-detail"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("redacted"));
    }

    [Fact]
    public async Task PayloadsLogged_WhenLoggingEnabled()
    {
        CapturingLogger<Agent> logger = await RunFailingToolTurn(logToolPayloads: true);

        // Both the input arguments and the error content appear verbatim.
        Assert.Contains(logger.Entries, e => e.Message.Contains("s3cr3t"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("boom-error-detail"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("redacted"));
    }
}
