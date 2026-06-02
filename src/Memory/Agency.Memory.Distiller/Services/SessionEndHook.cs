using Agency.Harness.Hooks;
using Agency.Memory.Common.Jobs;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Builds the <c>OnSessionEnd</c> callback that enqueues a terminal
/// <see cref="DistillationTrigger.SessionDisposed"/> distillation job when a
/// <see cref="Agency.Harness.ChatSession"/> is disposed.
/// </summary>
internal static class SessionEndHook
{
    /// <summary>
    /// Creates the <c>OnSessionEnd</c> callback bound to <paramref name="channels"/>.
    /// </summary>
    /// <param name="channels">Registry providing per-session channel writers.</param>
    /// <returns>
    /// A delegate that enqueues a <see cref="DistillationJob"/> with
    /// <see cref="DistillationTrigger.SessionDisposed"/> when invoked.
    /// </returns>
    internal static Func<SessionEndedHookContext, CancellationToken, Task> Create(
        ChannelSessionRegistry channels) =>
        (hookCtx, _) =>
        {
            Agency.Harness.Contexts.Context ctx = hookCtx.AgentContext;
            string userId = ctx.User.Id ?? string.Empty;
            string sessionId = ctx.Session.Id ?? string.Empty;
            var job = new DistillationJob(
                UserId: userId,
                SessionId: sessionId,
                Trigger: DistillationTrigger.SessionDisposed,
                UpToTurnIndex: ctx.Conversation.Messages.Count);
            channels.GetOrCreateWriter(userId, sessionId).TryWrite(job);
            return Task.CompletedTask;
        };
}
