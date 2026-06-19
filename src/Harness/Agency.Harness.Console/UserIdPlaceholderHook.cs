using Agency.Harness.Hooks;
using System.Text.Json;

namespace Agency.Harness.Console;

/// <summary>
/// An <c>OnPreToolUse</c> hook that substitutes the literal placeholder <c>{userId}</c> in tool-call
/// arguments with the session's resolved user id (<see cref="Contexts.UserSpecificContext.Id"/>).
/// </summary>
/// <remarks>
/// The host owns user identity, not the model: tools (e.g. the memory MCP server) advertise a required
/// <c>UserId</c> field, and the model is instructed to pass the placeholder <c>{userId}</c> rather than
/// invent a value. This hook rewrites that placeholder to the real id before invocation, so the model can
/// never supply a wrong or fabricated id. The substitution activates only when the placeholder is present,
/// so it is a no-op for every other tool. The resolved id is a GUID, which is JSON-safe to inline.
/// </remarks>
internal static class UserIdPlaceholderHook
{
    internal const string Placeholder = "{userId}";

    internal static AgentHooks Hooks { get; } = new AgentHooks
    {
        OnPreToolUse = static (ctx, _) =>
        {
            string? userId = ctx.AgentContext.User.Id;
            if (string.IsNullOrEmpty(userId))
            {
                return Task.FromResult(PreToolUseDecision.Allowed);
            }

            string raw = ctx.Input.GetRawText();
            if (raw.Contains(Placeholder, StringComparison.Ordinal) == false)
            {
                return Task.FromResult(PreToolUseDecision.Allowed);
            }

            string rewritten = raw.Replace(Placeholder, userId, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(rewritten);
            return Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Rewrite(doc.RootElement.Clone()));
        }
    };
}
