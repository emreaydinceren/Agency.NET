using Agency.Harness.Hooks;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Builds the <c>OnSessionStarted</c> callback that registers the agent's live conversation
/// manager with the <see cref="IConversationManagerRegistry"/>, keyed by the session id, so the
/// distiller can read the session's turns. Registration is idempotent across turns because the
/// session id and the conversation-manager instance are stable for the lifetime of a session.
/// </summary>
internal static class ConversationRegistrationHook
{
    /// <summary>Creates the registration callback bound to <paramref name="registry"/>.</summary>
    internal static Func<SessionStartedHookContext, CancellationToken, Task> Create(
        IConversationManagerRegistry registry) =>
        (hookCtx, _) =>
        {
            string sessionId = hookCtx.AgentContext.Session.Id ?? string.Empty;
            registry.Register(sessionId, hookCtx.AgentContext.Conversation);
            return Task.CompletedTask;
        };
}
