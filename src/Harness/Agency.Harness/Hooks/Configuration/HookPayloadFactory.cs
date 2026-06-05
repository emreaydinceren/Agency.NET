using Agency.Harness.Hooks;
using Microsoft.Extensions.AI;

namespace Agency.Harness.Hooks.Configuration;

internal static class HookPayloadFactory
{
    public static HookPayload ForPreToolUse(PreToolUseHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "PreToolUse",
            Cwd = Environment.CurrentDirectory,
            IterationCount = ctx.AgentContext.IterationCount,
            TotalCostUsd = (double)ctx.AgentContext.TotalCostUsd,
            ToolName = ctx.ToolName,
            ToolInput = ctx.Input,
        };

    public static HookPayload ForPostToolUse(PostToolUseHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "PostToolUse",
            Cwd = Environment.CurrentDirectory,
            IterationCount = ctx.AgentContext.IterationCount,
            TotalCostUsd = (double)ctx.AgentContext.TotalCostUsd,
            ToolName = ctx.ToolName,
            ToolInput = ctx.Input,
            ToolResponse = new ToolResponsePayload(ctx.Result.Content, ctx.Result.IsError),
        };

    public static HookPayload ForUserPromptSubmit(UserPromptSubmitHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "UserPromptSubmit",
            Cwd = Environment.CurrentDirectory,
            IterationCount = ctx.AgentContext.IterationCount,
            TotalCostUsd = (double)ctx.AgentContext.TotalCostUsd,
            Prompt = ctx.Prompt,
        };

    public static HookPayload ForSessionStarted(SessionStartedHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "SessionStarted",
            Cwd = Environment.CurrentDirectory,
        };

    public static HookPayload ForSessionEnded(SessionEndedHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "SessionEnded",
            Cwd = Environment.CurrentDirectory,
        };

    public static HookPayload ForStop(StopHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "Stop",
            Cwd = Environment.CurrentDirectory,
            IterationCount = ctx.AgentContext.IterationCount,
            TotalCostUsd = (double)ctx.AgentContext.TotalCostUsd,
        };

    public static HookPayload ForAssistantTurn(AssistantTurnHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "AssistantTurn",
            Cwd = Environment.CurrentDirectory,
            IterationCount = ctx.AgentContext.IterationCount,
            TotalCostUsd = (double)ctx.AgentContext.TotalCostUsd,
            Message = ExtractText(ctx.Message),
        };

    public static HookPayload ForPreIteration(PreIterationHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "PreIteration",
            Cwd = Environment.CurrentDirectory,
        };

    public static HookPayload ForPostToolBatch(PostToolBatchHookContext ctx) =>
        new()
        {
            SessionId = ctx.AgentContext.Session.Id,
            HookEventName = "PostToolBatch",
            Cwd = Environment.CurrentDirectory,
        };

    private static string? ExtractText(ChatMessage msg)
    {
        string text = string.Concat(msg.Contents.OfType<TextContent>().Select(static t => t.Text));
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
