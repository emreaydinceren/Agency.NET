using System.Threading.Channels;
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test.Services;

/// <summary>
/// Tests for <see cref="SessionEndHook"/> (A3: SessionDisposed trigger).
/// </summary>
public sealed class SessionEndHookTests
{
    private static (ChannelSessionRegistry Channels, Func<SessionEndedHookContext, CancellationToken, Task> Callback)
        CreateCallback()
    {
        var options = Options.Create(new DistillerOptions());
        var channels = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        Func<SessionEndedHookContext, CancellationToken, Task> callback = SessionEndHook.Create(channels);
        return (channels, callback);
    }

    private static Context MakeContext(
        string userId = "u1",
        string sessionId = "s1",
        int messageCount = 5)
    {
        var convo = new InMemoryConversationManager();
        for (int i = 0; i < messageCount; i++)
        {
            convo.Append(new Microsoft.Extensions.AI.ChatMessage(
                i % 2 == 0
                    ? Microsoft.Extensions.AI.ChatRole.User
                    : Microsoft.Extensions.AI.ChatRole.Assistant,
                $"msg {i}"));
        }

        return new Context
        {
            Query = new QueryContext { Prompt = "test" },
            User = new UserSpecificContext { Id = userId },
            Session = new SessionContext { Id = sessionId },
            Conversation = convo,
        };
    }

    /// <summary>
    /// The callback enqueues a job with <see cref="DistillationTrigger.SessionDisposed"/>
    /// and <c>UpToTurnIndex</c> equal to the conversation's message count at call time.
    /// </summary>
    [Fact]
    public async Task Create_Callback_EnqueuesSessionDisposedJob_WithCorrectTurnIndex()
    {
        var (channels, callback) = CreateCallback();
        Context ctx = MakeContext(userId: "user1", sessionId: "sess1", messageCount: 7);
        var hookCtx = new SessionEndedHookContext("sess1", ctx);

        await callback(hookCtx, CancellationToken.None);

        Channel<DistillationJob> ch = channels.GetOrCreate("user1", "sess1");
        Assert.True(ch.Reader.TryRead(out DistillationJob? job));
        Assert.Equal(DistillationTrigger.SessionDisposed, job!.Trigger);
        Assert.Equal(7, job.UpToTurnIndex);
        Assert.Equal("user1", job.UserId);
        Assert.Equal("sess1", job.SessionId);
    }

    /// <summary>
    /// The callback enqueues exactly one job per invocation.
    /// </summary>
    [Fact]
    public async Task Create_Callback_EnqueuesExactlyOneJob()
    {
        var (channels, callback) = CreateCallback();
        Context ctx = MakeContext();
        var hookCtx = new SessionEndedHookContext("s1", ctx);

        await callback(hookCtx, CancellationToken.None);

        Channel<DistillationJob> ch = channels.GetOrCreate("u1", "s1");
        Assert.True(ch.Reader.TryRead(out _));
        Assert.False(ch.Reader.TryRead(out _), "Should enqueue exactly one job.");
    }

    /// <summary>
    /// When <c>User.Id</c> and <c>Session.Id</c> are null, empty-string fallbacks are used and
    /// no exception is thrown.
    /// </summary>
    [Fact]
    public async Task Create_Callback_NullIds_UsesEmptyStringFallback()
    {
        var (channels, callback) = CreateCallback();
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "test" },
            User = new UserSpecificContext { Id = null },
            Session = new SessionContext { Id = null },
            Conversation = new InMemoryConversationManager(),
        };
        var hookCtx = new SessionEndedHookContext(string.Empty, ctx);

        var ex = await Record.ExceptionAsync(() => callback(hookCtx, CancellationToken.None));
        Assert.Null(ex);

        // Job should be enqueued under the empty-string session channel.
        Channel<DistillationJob> ch = channels.GetOrCreate(string.Empty, string.Empty);
        Assert.True(ch.Reader.TryRead(out DistillationJob? job));
        Assert.Equal(DistillationTrigger.SessionDisposed, job!.Trigger);
    }
}
