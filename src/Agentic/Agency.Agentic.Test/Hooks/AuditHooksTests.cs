namespace Agency.Agentic.Hooks.Tests;

using System.Text.Json;
using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using Microsoft.Extensions.Logging;

/// <summary>Verifies AuditHooks.ForLogger logs pre/post tool use at Information level.</summary>
public sealed class AuditHooksTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static PreToolUseHookContext MakePreCtx(string toolName = "search") =>
        new(toolName,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
            new Context { Query = new QueryContext { Prompt = "test" } });

    private static PostToolUseHookContext MakePostCtx(string toolName = "search", bool isError = false) =>
        new(toolName,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
            new ToolResult("content", isError),
            new Context { Query = new QueryContext { Prompt = "test" } });

    [Fact]
    public async Task ForLogger_OnPreToolUse_LogsToolNameAtInformation()
    {
        var logger = new CapturingLogger();
        AgentHooks hooks = AuditHooks.ForLogger(logger);
        await hooks.OnPreToolUse!(MakePreCtx("search"), CancellationToken.None);
        Assert.Single(logger.Entries);
        (LogLevel level, string msg) = logger.Entries[0];
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("search", msg);
    }

    [Fact]
    public async Task ForLogger_OnPostToolUse_LogsIsErrorFalseAtInformation()
    {
        var logger = new CapturingLogger();
        AgentHooks hooks = AuditHooks.ForLogger(logger);
        await hooks.OnPostToolUse!(MakePostCtx(isError: false), CancellationToken.None);
        Assert.Single(logger.Entries);
        (LogLevel level, string msg) = logger.Entries[0];
        Assert.Equal(LogLevel.Information, level);
        Assert.NotEmpty(msg);
    }

    [Fact]
    public async Task ForLogger_OnPostToolUse_LogsWhenToolFails()
    {
        var logger = new CapturingLogger();
        AgentHooks hooks = AuditHooks.ForLogger(logger);
        await hooks.OnPostToolUse!(MakePostCtx(isError: true), CancellationToken.None);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void ForLogger_OnSessionStarted_IsNull()
    {
        var logger = new CapturingLogger();
        AgentHooks hooks = AuditHooks.ForLogger(logger);
        Assert.Null(hooks.OnSessionStarted);
    }

    [Fact]
    public async Task ForLogger_OnPreToolUse_ReturnsAllow()
    {
        var logger = new CapturingLogger();
        AgentHooks hooks = AuditHooks.ForLogger(logger);
        PreToolUseDecision result = await hooks.OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }
}