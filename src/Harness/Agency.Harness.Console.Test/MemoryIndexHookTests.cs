using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;
using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="MemoryIndexHook"/>, the OnSessionStarted hook that primes every turn with
/// an index of what's stored in the "memory" MCP server, so the model doesn't have to gamble on whether
/// calling <c>recall</c> is worthwhile.
/// </summary>
public sealed class MemoryIndexHookTests
{
    private sealed class FakeListGlobalKeysTool(string content, bool isError = false) : ITool
    {
        public int InvokeCount { get; private set; }

        public JsonElement? LastInput { get; private set; }

        public ToolDefinition Definition { get; } = new("list_global_keys", string.Empty, default);

        public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        {
            this.InvokeCount++;
            this.LastInput = input.Clone();
            return Task.FromResult(new ToolResult(content, isError));
        }
    }

    private sealed class ThrowingTool : ITool
    {
        public ToolDefinition Definition { get; } = new("list_global_keys", string.Empty, default);

        public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct) =>
            throw new InvalidOperationException("MCP server unavailable.");
    }

    /// <summary>
    /// Builds a test <see cref="Context"/> with a real <see cref="ToolRegistry"/> containing a
    /// <c>list_global_keys</c> stub, so the hook's registry guard (checking that the tool is still
    /// advertised) passes by default. Pass <paramref name="listGlobalKeysToolEnabled"/> as
    /// <see langword="false"/> to simulate a <c>/mcp-toggle</c>-disabled server.
    /// </summary>
    private static Context MakeContext(string? userId = "user-1", bool listGlobalKeysToolEnabled = true)
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeListGlobalKeysTool("{}"));
        if (!listGlobalKeysToolEnabled)
        {
            registry.DisableToolByUser(MemoryIndexHook.ListGlobalKeysToolName);
        }

        return new()
        {
            Query = new QueryContext { Prompt = "p" },
            User = new UserSpecificContext { Id = userId },
            Tools = new ToolContext { Registry = registry },
        };
    }

    private static Task FireOnSessionStarted(AgentHooks hooks, Context ctx) =>
        hooks.OnSessionStarted!(new SessionStartedHookContext("session-1", ctx), CancellationToken.None);

    /// <summary>No "memory" server tool available → the hook is a no-op (null <c>OnSessionStarted</c>).</summary>
    [Fact]
    public void Build_NullTool_ReturnsNoOpHooks()
    {
        AgentHooks hooks = MemoryIndexHook.Build(null);

        Assert.Null(hooks.OnSessionStarted);
    }

    /// <summary>A non-empty index is rendered as a single fact and appended to <c>Knowledge.Facts</c>.</summary>
    [Fact]
    public async Task OnSessionStarted_NonEmptyIndex_AppendsFact()
    {
        var tool = new FakeListGlobalKeysTool("""{"Personal":{"Keys":["FavouriteDessert"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);

        Assert.Single(ctx.Knowledge.Facts);
        Assert.Contains("Personal|FavouriteDessert", ctx.Knowledge.Facts[0]);
        Assert.Contains("recall", ctx.Knowledge.Facts[0]);
    }

    /// <summary>The user id is threaded into the tool call's scope argument, not hardcoded or omitted.</summary>
    [Fact]
    public async Task OnSessionStarted_PassesResolvedUserIdAsScope()
    {
        var tool = new FakeListGlobalKeysTool("{}");
        AgentHooks hooks = MemoryIndexHook.Build(tool);

        await FireOnSessionStarted(hooks, MakeContext(userId: "abc-123"));

        Assert.Equal(1, tool.InvokeCount);
        string userId = tool.LastInput!.Value.GetProperty("scope").GetProperty("userId").GetString()!;
        Assert.Equal("abc-123", userId);
    }

    /// <summary>
    /// An empty store still gets a fact carrying the write-side instruction. A fresh user is exactly
    /// the case that needs it: with nothing stored and nothing in the system prompt, "save durable
    /// facts" would exist only inside the memorize tool's description, which the model reads only
    /// after it has already decided to reach for the tool.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_EmptyIndex_StillAddsWritePolicyFact()
    {
        var tool = new FakeListGlobalKeysTool("{}");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);

        string fact = Assert.Single(ctx.Knowledge.Facts);
        Assert.Contains("memorize", fact, StringComparison.Ordinal);
        Assert.Contains("Nothing is stored for this user yet.", fact, StringComparison.Ordinal);
    }

    /// <summary>
    /// The write-side instruction rides on the same fact as the index, so it must survive alongside
    /// stored keys rather than being displaced by them.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_NonEmptyIndex_AlsoCarriesWritePolicy()
    {
        var tool = new FakeListGlobalKeysTool("""{"Personal":{"Keys":["FavouriteDessert"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);

        string fact = Assert.Single(ctx.Knowledge.Facts);
        Assert.Contains("memorize", fact, StringComparison.Ordinal);
        Assert.Contains("Personal|FavouriteDessert", fact, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing is stored", fact, StringComparison.Ordinal);
    }

    /// <summary>No resolved user id → the tool is never called (nothing valid to scope the lookup to).</summary>
    [Fact]
    public async Task OnSessionStarted_NoUserId_DoesNotInvokeTool()
    {
        var tool = new FakeListGlobalKeysTool("""{"Personal":{"Keys":["X"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);

        await FireOnSessionStarted(hooks, MakeContext(userId: null));

        Assert.Equal(0, tool.InvokeCount);
    }

    /// <summary>
    /// OnSessionStarted re-fires on every turn (once per ChatAsync call). A second firing with an updated
    /// index must replace the previous turn's fact, not accumulate a duplicate copy alongside it.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_FiresAcrossTwoTurns_ReplacesPriorFact_DoesNotDuplicate()
    {
        var tool = new FakeListGlobalKeysTool("""{"Personal":{"Keys":["FavouriteDessert"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);
        await FireOnSessionStarted(hooks, ctx);

        Assert.Single(ctx.Knowledge.Facts);
    }

    /// <summary>A tool-invocation failure (crashed subprocess, broken pipe) is swallowed, not surfaced as a turn failure.</summary>
    [Fact]
    public async Task OnSessionStarted_ToolThrows_DoesNotThrow_AndAddsNoFact()
    {
        AgentHooks hooks = MemoryIndexHook.Build(new ThrowingTool());
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);

        Assert.Empty(ctx.Knowledge.Facts);
    }

    /// <summary>An error result from the tool (e.g. validation failure) is treated as "no index", not surfaced as a fact.</summary>
    [Fact]
    public async Task OnSessionStarted_ToolReturnsError_AddsNoFact()
    {
        var tool = new FakeListGlobalKeysTool("Error: scope.userId is required.", isError: true);
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);

        Assert.Empty(ctx.Knowledge.Facts);
    }

    /// <summary>Multiple domains/keys are each rendered as a distinct <c>domain|key</c> pair in the single fact.</summary>
    [Fact]
    public async Task OnSessionStarted_MultipleDomainsAndKeys_AllListedInFact()
    {
        var tool = new FakeListGlobalKeysTool(
            """{"Personal":{"Keys":["FavouriteDessert","Address"],"Tags":[]},"Work":{"Keys":["ExpensePolicy"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);

        string fact = Assert.Single(ctx.Knowledge.Facts);
        Assert.Contains("Personal|FavouriteDessert", fact);
        Assert.Contains("Personal|Address", fact);
        Assert.Contains("Work|ExpensePolicy", fact);
    }

    /// <summary>
    /// When <c>/mcp-toggle</c> has disabled <c>list_global_keys</c> in the registry, the hook must
    /// short-circuit before invoking the tool directly — otherwise the toggle would have no effect,
    /// since the tool is called by reference rather than dispatched through the registry.
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_ListGlobalKeysToolDisabled_DoesNotInvokeToolOrAddFact()
    {
        var tool = new FakeListGlobalKeysTool("""{"Personal":{"Keys":["FavouriteDessert"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext(listGlobalKeysToolEnabled: false);

        await FireOnSessionStarted(hooks, ctx);

        Assert.Equal(0, tool.InvokeCount);
        Assert.Empty(ctx.Knowledge.Facts);
    }

    /// <summary>
    /// A fact injected on a prior turn must be removed on the next turn once the tool has been
    /// disabled mid-session — a toggle-off takes effect immediately rather than stranding a stale
    /// fact pointing the model at a tool that now answers "Tool is disabled."
    /// </summary>
    [Fact]
    public async Task OnSessionStarted_ToolDisabledAfterPriorFact_RemovesStaleFactOnNextTurn()
    {
        var tool = new FakeListGlobalKeysTool("""{"Personal":{"Keys":["FavouriteDessert"],"Tags":[]}}""");
        AgentHooks hooks = MemoryIndexHook.Build(tool);
        Context ctx = MakeContext();

        await FireOnSessionStarted(hooks, ctx);
        Assert.Single(ctx.Knowledge.Facts);

        ctx.Tools.Registry.DisableToolByUser(MemoryIndexHook.ListGlobalKeysToolName);
        await FireOnSessionStarted(hooks, ctx);

        Assert.Empty(ctx.Knowledge.Facts);
    }
}
