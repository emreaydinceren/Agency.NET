using Microsoft.Extensions.Logging;

namespace Agency.Agentic.Hooks;

/// <summary>Pre-built hooks that log tool invocations for auditing.</summary>
public static class AuditHooks
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
            logger.LogInformation(
                "PreToolUse: tool={Tool} input={Input}",
                ctx.ToolName,
                ctx.Input);
            return Task.FromResult(PreToolUseDecision.Allowed);
        },
        OnPostToolUse = (ctx, _) =>
        {
            logger.LogInformation(
                "PostToolUse: tool={Tool} error={IsError}",
                ctx.ToolName,
                ctx.Result.IsError);
            return Task.CompletedTask;
        },
    };
}