using System.Text.Json;
using System.Threading;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies that <see cref="HookRegistry"/> correctly projects <see cref="HooksOptions"/>
/// into an <see cref="AgentHooks"/> instance:
/// null delegates when no config, non-null when configured, matcher filtering,
/// deny-wins across groups, non-tool event dispatch, and Empty sentinel.
/// </summary>
public sealed class HookRegistryProjectionTests
{
    // ── Fake infrastructure ──────────────────────────────────────────────────

    private sealed class RecordingHandler : IHookHandler
    {
        private int _callCount;
        public int CallCount => _callCount;
        public HookHandlerOutput Output { get; init; } =
            new HookHandlerOutput(HookExitCodes.Ok, null, null, null);

        public Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(this.Output);
        }
    }

    private sealed class FakeFactory : IHookHandlerFactory
    {
        private readonly RecordingHandler _handler;
        public FakeFactory(RecordingHandler handler) => _handler = handler;
        public IHookHandler Create(HookHandlerConfig config) => _handler;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HooksOptions OneGroup(HookEventName @event, string matcher)
    {
        var options = new HooksOptions();
        options.Hooks[@event] = [new HookMatcherGroupConfig
        {
            Matcher = matcher,
            Hooks = [new HookHandlerConfig { Type = HookHandlerKind.Command, Command = "fake" }]
        }];
        return options;
    }

    private static HooksOptions TwoGroups(HookEventName @event, string matcher)
    {
        var options = new HooksOptions();
        options.Hooks[@event] =
        [
            new HookMatcherGroupConfig
            {
                Matcher = matcher,
                Hooks = [new HookHandlerConfig { Type = HookHandlerKind.Command, Command = "fake1" }]
            },
            new HookMatcherGroupConfig
            {
                Matcher = matcher,
                Hooks = [new HookHandlerConfig { Type = HookHandlerKind.Command, Command = "fake2" }]
            },
        ];
        return options;
    }

    private static Context MinimalContext() =>
        new Context { Query = new QueryContext { Prompt = "test" } };

    private static JsonElement EmptyJson() =>
        JsonDocument.Parse("{}").RootElement.Clone();

    // ── Test 1 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Project_NoConfig_AllDelegatesNull()
    {
        var handler = new RecordingHandler();
        var factory = new FakeFactory(handler);
        AgentHooks hooks = new HookRegistry(new HooksOptions(), factory, null).ToAgentHooks();

        Assert.Null(hooks.OnPreToolUse);
        Assert.Null(hooks.OnPostToolUse);
        Assert.Null(hooks.OnSessionStarted);
        Assert.Null(hooks.OnUserPromptSubmit);
        Assert.Null(hooks.OnPreIteration);
        Assert.Null(hooks.OnPostToolBatch);
        Assert.Null(hooks.OnAssistantTurn);
        Assert.Null(hooks.OnStop);
        Assert.Null(hooks.OnSessionEnd);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Project_PreToolUseConfigured_DelegateNonNull()
    {
        var handler = new RecordingHandler();
        var factory = new FakeFactory(handler);
        HooksOptions options = OneGroup(HookEventName.PreToolUse, "*");

        AgentHooks hooks = new HookRegistry(options, factory, null).ToAgentHooks();

        Assert.NotNull(hooks.OnPreToolUse);
        Assert.Null(hooks.OnPostToolUse);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_MatcherFiltersByToolName()
    {
        var handler = new RecordingHandler();
        var factory = new FakeFactory(handler);
        // Matcher only matches "Bash", but we will invoke with tool "Edit"
        HooksOptions options = OneGroup(HookEventName.PreToolUse, "Bash");

        AgentHooks hooks = new HookRegistry(options, factory, null).ToAgentHooks();
        Assert.NotNull(hooks.OnPreToolUse);

        var ctx = new PreToolUseHookContext("Edit", EmptyJson(), MinimalContext());
        PreToolUseDecision result = await hooks.OnPreToolUse!(ctx, CancellationToken.None);

        Assert.IsType<PreToolUseDecision.Allow>(result);
        Assert.Equal(0, handler.CallCount);
    }

    // ── Test 4 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_DenyWins_AcrossTwoGroups()
    {
        var allowHandler = new RecordingHandler
        {
            Output = new HookHandlerOutput(HookExitCodes.Ok, null, null, null)
        };
        var denyHandler = new RecordingHandler
        {
            Output = new HookHandlerOutput(HookExitCodes.BlockingDeny, null, "denied by second", null)
        };

        // Two groups: first returns allow, second returns deny.
        // Use a sequencing factory so each group gets a different handler.
        var handlers = new Queue<RecordingHandler>([allowHandler, denyHandler]);

        var seqFactory = new SequencedFactory(handlers);
        HooksOptions options = TwoGroups(HookEventName.PreToolUse, "*");

        AgentHooks hooks = new HookRegistry(options, seqFactory, null).ToAgentHooks();
        Assert.NotNull(hooks.OnPreToolUse);

        var ctx = new PreToolUseHookContext("Bash", EmptyJson(), MinimalContext());
        PreToolUseDecision result = await hooks.OnPreToolUse!(ctx, CancellationToken.None);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    private sealed class SequencedFactory : IHookHandlerFactory
    {
        private readonly Queue<RecordingHandler> _queue;
        public SequencedFactory(Queue<RecordingHandler> queue) => _queue = queue;
        public IHookHandler Create(HookHandlerConfig config) =>
            _queue.Count > 0 ? _queue.Dequeue() : new RecordingHandler();
    }

    // ── Test 5 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_NonToolEvent_AwaitsAllHandlers()
    {
        var handler = new RecordingHandler();
        var factory = new FakeFactory(handler);
        HooksOptions options = OneGroup(HookEventName.PostToolUse, "*");

        AgentHooks hooks = new HookRegistry(options, factory, null).ToAgentHooks();
        Assert.NotNull(hooks.OnPostToolUse);

        var toolResult = new ToolResult("ok");
        var ctx = new PostToolUseHookContext("Bash", EmptyJson(), toolResult, MinimalContext());
        await hooks.OnPostToolUse!(ctx, CancellationToken.None);

        Assert.True(handler.CallCount >= 1);
    }

    // ── Test 6 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_ProducesAgentHooksNone()
    {
        AgentHooks hooks = HookRegistry.Empty.ToAgentHooks();

        Assert.Null(hooks.OnPreToolUse);
        Assert.Null(hooks.OnPostToolUse);
        Assert.Null(hooks.OnSessionStarted);
        Assert.Null(hooks.OnUserPromptSubmit);
        Assert.Null(hooks.OnPreIteration);
        Assert.Null(hooks.OnPostToolBatch);
        Assert.Null(hooks.OnAssistantTurn);
        Assert.Null(hooks.OnStop);
        Assert.Null(hooks.OnSessionEnd);
    }
}
