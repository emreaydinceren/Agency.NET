using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Memory.Common.Records;
using Agency.Memory.Distiller.Test.Stubs;
using Agency.Memory.Distiller.Tools;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Distiller.Test.Tools;

/// <summary>
/// Tests for <see cref="SetFocusTool"/> (Spec §6.7.1, C.6).
/// </summary>
public sealed class SetFocusToolTests
{
    private static SetFocusTool CreateTool(out Context ctx, InMemoryMemoryStore? store = null, string userId = "u1")
    {
        ctx = new Context
        {
            Query = new QueryContext { Prompt = "test" },
        };

        var capturedCtx = ctx; // capture for lambda
        return new SetFocusTool(store ?? new InMemoryMemoryStore(), userId, () => capturedCtx);
    }

    /// <summary>Invoking the tool updates Context.Focus.</summary>
    [Fact]
    public async Task Invoke_UpdatesContextFocus()
    {
        SetFocusTool tool = CreateTool(out Context ctx, null, "u1");

        JsonElement input = JsonDocument.Parse("""
            {"title":"Auth Debugging","domain":"Debugging","tags":["ssl","dns"]}
            """).RootElement;

        await tool.InvokeAsync(input, CancellationToken.None);

        Assert.Equal("Auth Debugging", ctx.Focus.Title);
        Assert.Equal("Debugging", ctx.Focus.Domain);
        Assert.Equal(["ssl", "dns"], ctx.Focus.Tags);
    }

    /// <summary>Setting the same values twice is a no-op and returns the prior focus.</summary>
    [Fact]
    public async Task Invoke_SameValuesTwice_IsNoOp_ReturnsPriorFocus()
    {
        SetFocusTool tool = CreateTool(out Context ctx, null, "u1");

        JsonElement input = JsonDocument.Parse("""
            {"title":"Auth Debugging","domain":"Debugging","tags":["ssl"]}
            """).RootElement;

        await tool.InvokeAsync(input, CancellationToken.None);
        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.Contains("unchanged", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Auth Debugging", ctx.Focus.Title); // unchanged
    }

    /// <summary>GetDefinitionAsync lists known domains for the user in the description.</summary>
    [Fact]
    public async Task GetDefinitionAsync_ListsDistinctDomainsForUser()
    {
        var store = new InMemoryMemoryStore();

        // Seed some records with different domains.
        await store.UpsertAsync(MemoryRecord.Create(
            "r1", "u1", null, ContentType.Fact, "Preferences", "LangKey", "Python", "V", [], 0.5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        await store.UpsertAsync(MemoryRecord.Create(
            "r2", "u1", null, ContentType.Memory, "Debugging", "SslKey", "SSL", "V", [], 0.7,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        SetFocusTool tool = CreateTool(out _, store, "u1");

        Agency.Llm.Common.Tools.ToolDefinition def = await tool.GetDefinitionAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Preferences", def.Description);
        Assert.Contains("Debugging", def.Description);
    }

    /// <summary>
    /// The sync Definition property returns the static fallback — it does NOT query the store.
    /// </summary>
    [Fact]
    public async Task Definition_ReturnsStaticFallback_DoesNotContainDomains()
    {
        var store = new InMemoryMemoryStore();

        await store.UpsertAsync(MemoryRecord.Create(
            "r1", "u1", null, ContentType.Fact, "Preferences", "LangKey", "Python", "V", [], 0.5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        SetFocusTool tool = CreateTool(out _, store, "u1");

        // The sync property must NOT contain the seeded domain — it is the static fallback.
        Assert.DoesNotContain("Preferences", tool.Definition.Description);
    }
}
