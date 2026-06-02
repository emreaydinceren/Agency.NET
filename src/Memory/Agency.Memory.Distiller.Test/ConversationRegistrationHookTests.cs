using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Distiller.Services;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Tests for <see cref="ConversationRegistrationHook"/> — verifies that the
/// <c>OnSessionStarted</c> callback registers the live conversation manager in the
/// registry under the correct key so the distiller can look it up by session id.
/// </summary>
public sealed class ConversationRegistrationHookTests
{
    /// <summary>
    /// When <c>ctx.Session.Id</c> is a non-null string, the callback registers the conversation
    /// manager under that id, and <c>registry.Get</c> returns the same instance.
    /// </summary>
    [Fact]
    public async Task Create_RegistersConversation_UnderSessionId()
    {
        var registry = new InMemoryConversationManagerRegistry();
        var convo = new InMemoryConversationManager();
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "hi" },
            Conversation = convo,
            Session = new SessionContext { Id = "s1" },
        };

        Func<SessionStartedHookContext, CancellationToken, Task> callback =
            ConversationRegistrationHook.Create(registry);

        await callback(new SessionStartedHookContext("s1", ctx), CancellationToken.None);

        Assert.Same(convo, registry.Get("s1"));
    }

    /// <summary>
    /// When <c>ctx.Session.Id</c> is null, the callback registers under <see cref="string.Empty"/>,
    /// matching the key expression used by the timer and distiller.
    /// </summary>
    [Fact]
    public async Task Create_WhenSessionIdIsNull_RegistersUnderEmptyString()
    {
        var registry = new InMemoryConversationManagerRegistry();
        var convo = new InMemoryConversationManager();
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "hi" },
            Conversation = convo,
            Session = new SessionContext { Id = null },
        };

        Func<SessionStartedHookContext, CancellationToken, Task> callback =
            ConversationRegistrationHook.Create(registry);

        await callback(new SessionStartedHookContext(string.Empty, ctx), CancellationToken.None);

        Assert.Same(convo, registry.Get(string.Empty));
    }
}
