using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Distiller.Test.Stubs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test.Services;

/// <summary>
/// Tests for <see cref="MemorySessionTools"/> (A2: memory tool registration at session start).
/// </summary>
public sealed class MemorySessionToolsTests
{
    private static (Context Ctx, ChannelSessionRegistry Channels, InMemoryMemoryStore Store)
        CreateDependencies(string userId = "u1", string sessionId = "s1")
    {
        var options = Options.Create(new DistillerOptions());
        var channels = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        var store = new InMemoryMemoryStore();

        var registry = new ToolRegistry();
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "test" },
            User = new UserSpecificContext { Id = userId },
            Session = new SessionContext { Id = sessionId },
            Tools = new ToolContext { Registry = registry },
            Conversation = new InMemoryConversationManager(),
        };

        return (ctx, channels, store);
    }

    /// <summary>
    /// After <see cref="MemorySessionTools.RegisterInto"/>, the registry contains both
    /// <c>MarkGoalComplete</c> and <c>SetFocus</c> tool definitions.
    /// </summary>
    [Fact]
    public async Task RegisterInto_AddsBothToolsToRegistry()
    {
        var (ctx, channels, store) = CreateDependencies();

        await MemorySessionTools.RegisterInto(ctx, channels, store, TestContext.Current.CancellationToken);

        System.Collections.Generic.IReadOnlyList<Agency.Llm.Common.Tools.ToolDefinition> defs =
            ctx.Tools.Registry.ListDefinitions();

        Assert.Contains(defs, d => d.Name == "MarkGoalComplete");
        Assert.Contains(defs, d => d.Name == "SetFocus");
    }

    /// <summary>
    /// Calling <see cref="MemorySessionTools.RegisterInto"/> twice is idempotent: the registry
    /// still contains exactly two tool definitions (overwrite-by-name semantics).
    /// </summary>
    [Fact]
    public async Task RegisterInto_CalledTwice_IsIdempotent()
    {
        var (ctx, channels, store) = CreateDependencies();

        await MemorySessionTools.RegisterInto(ctx, channels, store, TestContext.Current.CancellationToken);
        await MemorySessionTools.RegisterInto(ctx, channels, store, TestContext.Current.CancellationToken);

        System.Collections.Generic.IReadOnlyList<Agency.Llm.Common.Tools.ToolDefinition> defs =
            ctx.Tools.Registry.ListDefinitions();

        // Register overwrites by name so there should be exactly one of each.
        Assert.Single(defs, d => d.Name == "MarkGoalComplete");
        Assert.Single(defs, d => d.Name == "SetFocus");
    }

    /// <summary>
    /// When <c>User.Id</c> and <c>Session.Id</c> are null, <see cref="MemorySessionTools.RegisterInto"/>
    /// substitutes empty strings and still registers both tools without throwing.
    /// </summary>
    [Fact]
    public async Task RegisterInto_NullUserAndSessionId_RegistrationSucceeds()
    {
        var options = Options.Create(new DistillerOptions());
        var channels = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        var store = new InMemoryMemoryStore();
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "test" },
            User = new UserSpecificContext { Id = null },
            Session = new SessionContext { Id = null },
            Tools = new ToolContext { Registry = new ToolRegistry() },
            Conversation = new InMemoryConversationManager(),
        };

        var ex = await Record.ExceptionAsync(() =>
            MemorySessionTools.RegisterInto(ctx, channels, store, TestContext.Current.CancellationToken));
        Assert.Null(ex);

        System.Collections.Generic.IReadOnlyList<Agency.Llm.Common.Tools.ToolDefinition> defs =
            ctx.Tools.Registry.ListDefinitions();
        Assert.Contains(defs, d => d.Name == "MarkGoalComplete");
        Assert.Contains(defs, d => d.Name == "SetFocus");
    }
}
