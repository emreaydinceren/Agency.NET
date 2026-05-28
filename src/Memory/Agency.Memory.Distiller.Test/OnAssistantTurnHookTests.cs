using Agency.Agentic;
using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using Agency.Memory.Common.Hooks;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Negative regression test: <see cref="AgentHooks.OnAssistantTurn"/> is timer-only.
/// Verifies that the hook does NOT enqueue a distillation job (Spec §14.9, C.7).
/// </summary>
public sealed class OnAssistantTurnHookTests
{
    /// <summary>
    /// The OnAssistantTurn hook should restart the inactivity timer and do nothing else.
    /// In particular, the distillation channel must remain empty after the hook fires.
    /// </summary>
    [Fact]
    public async Task OnAssistantTurn_RestartsInactivityTimer_DoesNotEnqueueJob()
    {
        var options = Options.Create(new DistillerOptions
        {
            // Very long timeout so the timer won't fire during the test.
            InactivityTimeout = TimeSpan.FromHours(1),
        });

        var registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);

        // Build the timer service manually.
        var timer = new InactivityTimerService(
            registry,
            options,
            TimeProvider.System,
            NullLogger<InactivityTimerService>.Instance);

        // Build the baseline hooks using the factory with a no-op retrieval callback.
        AgentHooks hooks = MemoryHookFactory.Build(
            retrievalCallback: (_, _) => Task.CompletedTask,
            timerRestartCallback: (hookCtx, _) =>
            {
                string userId = hookCtx.AgentContext.User.Id ?? string.Empty;
                string sessionId = "test-session";
                int turnIndex = hookCtx.AgentContext.Conversation.Messages.Count;
                timer.Restart(userId, sessionId, turnIndex);
                return Task.CompletedTask;
            });

        // Create a context and simulate an assistant turn.
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "hello" },
            User = new UserSpecificContext { Id = "u1" },
        };

        // Append some messages to simulate a turn.
        ctx.Conversation.Append(new ChatMessage(ChatRole.User, "Hello"));
        ctx.Conversation.Append(new ChatMessage(ChatRole.Assistant, "Hi there"));

        var assistantMessage = new ChatMessage(ChatRole.Assistant, "Hi there");
        var hookCtxInput = new AssistantTurnHookContext(assistantMessage, ctx);

        // Fire the OnAssistantTurn hook.
        if (hooks.OnAssistantTurn is not null)
        {
            await hooks.OnAssistantTurn(hookCtxInput, CancellationToken.None);
        }

        // Assert: the distillation channel for this session has NO jobs.
        var ch = registry.GetOrCreate("u1", "test-session");
        bool hasItem = ch.Reader.TryRead(out _);
        Assert.False(hasItem, "OnAssistantTurn must not enqueue a DistillationJob.");

        timer.Dispose();
    }
}
