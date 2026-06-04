using Agency.Harness.Contexts;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Tools;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Registers the per-session memory tools (<c>MarkGoalComplete</c>, <c>SetFocus</c>) into the
/// agent's tool registry at session start, bound to the live <see cref="Context"/>'s
/// user/session/turn state.
/// </summary>
/// <remarks>
/// Called from the <c>OnSessionStarted</c> hook so the tools are visible to the LLM from
/// the first iteration of the session. <see cref="Agency.Harness.Tools.ToolRegistry.Register"/>
/// overwrites by name, so calling this more than once is safe (idempotent).
/// </remarks>
internal static class MemorySessionTools
{
    /// <summary>
    /// Registers <c>MarkGoalComplete</c> and <c>SetFocus</c> tools into <paramref name="ctx"/>'s
    /// tool registry, binding them to the session identity in <paramref name="ctx"/>.
    /// </summary>
    /// <param name="ctx">The live session context whose <see cref="Context.Tools"/> registry receives the tools.</param>
    /// <param name="channels">Registry providing per-session channel writers for <c>MarkGoalComplete</c>.</param>
    /// <param name="store">Memory store used by <c>SetFocus</c> to enumerate known domains.</param>
    internal static void RegisterInto(Context ctx, ChannelSessionRegistry channels, IMemoryStore store)
    {
        string userId = ctx.User.Id ?? string.Empty;
        string sessionId = ctx.Session.Id ?? string.Empty;

        ctx.Tools.Registry.Register(
            new MarkGoalCompleteTool(channels, userId, sessionId, () => ctx.Conversation.Messages.Count, () => ctx.Focus));

        ctx.Tools.Registry.Register(
            new SetFocusTool(store, userId, () => ctx));
    }
}
