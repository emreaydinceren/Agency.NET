using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agency.Harness.Hooks;

/// <summary>Pre-built hooks that log tool invocations for auditing.</summary>
public static partial class AuditHooks
{
    /// <summary>
    /// Returns an <see cref="AgentHooks"/> that logs <c>OnPreToolUse</c> and
    /// <c>OnPostToolUse</c> events at <see cref="LogLevel.Information"/> via
    /// <paramref name="logger"/>. The <c>OnPreToolUse</c> delegate always returns
    /// <see cref="PreToolUseDecision.Allow"/>.
    /// </summary>
    public static AgentHooks ForLogger(ILogger logger) => new()
    {
        OnPreToolUse = (ctx, _) =>
        {
            LogPreToolUse(logger, ctx.ToolName, ctx.Input);
            return Task.FromResult(PreToolUseDecision.Allowed);
        },
        OnPostToolUse = (ctx, _) =>
        {
            LogPostToolUse(logger, ctx.ToolName, ctx.Result.IsError);
            return Task.CompletedTask;
        },
    };

    /// <summary>Logs a pre-tool-use audit entry.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "PreToolUse: tool={Tool} input={Input}")]
    private static partial void LogPreToolUse(ILogger logger, string tool, JsonElement input);

    /// <summary>Logs a post-tool-use audit entry.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "PostToolUse: tool={Tool} error={IsError}")]
    private static partial void LogPostToolUse(ILogger logger, string tool, bool isError);
}