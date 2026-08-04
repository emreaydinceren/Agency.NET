using System.Text.Json;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for <see cref="ToolRegistry"/>'s two-axis enable/disable API
/// (<see cref="IToolRegistry.DisableToolByUser"/>/<see cref="IToolRegistry.EnableToolByUser"/> and
/// <see cref="IToolRegistry.DisabledToolBySystem"/>/<see cref="IToolRegistry.EnableToolBySystem"/>).
/// This API had zero call sites and zero test coverage before <c>/mcp-toggle</c> activated it —
/// these are the first tests against the dormant scaffolding.
/// </summary>
public sealed class ToolRegistryEnableDisableTests
{
    /// <summary>
    /// Disabling a tool on the user axis removes it from <see cref="IToolRegistry.ListDefinitions"/>,
    /// the list advertised to the model.
    /// </summary>
    [Fact]
    public void DisableToolByUser_RemovesFromListDefinitions()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisableToolByUser("fake_tool");

        Assert.DoesNotContain(registry.ListDefinitions(), d => d.Name == "fake_tool");
    }

    /// <summary>
    /// A user-disabled tool still appears in <see cref="IToolRegistry.ListAllDefinitions"/> — the
    /// diagnostic view that reports every tool's enabled state — but with <c>Enabled == false</c>.
    /// </summary>
    [Fact]
    public void DisableToolByUser_RemainsInListAllDefinitions_ButReportedDisabled()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisableToolByUser("fake_tool");

        (bool Enabled, ToolDefinition Definition) entry =
            registry.ListAllDefinitions().Single(e => e.Definition.Name == "fake_tool");
        Assert.False(entry.Enabled);
    }

    /// <summary>
    /// Invoking a user-disabled tool by name is rejected immediately with a fixed error message,
    /// rather than reaching the underlying <see cref="ITool.InvokeAsync"/> implementation.
    /// </summary>
    [Fact]
    public async Task DisableToolByUser_InvokeAsync_ReturnsDisabledError()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisableToolByUser("fake_tool");

        ToolResult result = await registry.InvokeAsync(
            "fake_tool", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Tool is disabled.", result.Content);
    }

    /// <summary>
    /// <see cref="IToolRegistry.EnableToolByUser"/> reverses all three effects of
    /// <see cref="IToolRegistry.DisableToolByUser"/>: the tool reappears in
    /// <see cref="IToolRegistry.ListDefinitions"/>, is reported <c>Enabled == true</c> in
    /// <see cref="IToolRegistry.ListAllDefinitions"/>, and can be invoked successfully again.
    /// </summary>
    [Fact]
    public async Task EnableToolByUser_RestoresListDefinitions_ListAllDefinitions_AndInvokeAsync()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);
        registry.DisableToolByUser("fake_tool");

        registry.EnableToolByUser("fake_tool");

        Assert.Contains(registry.ListDefinitions(), d => d.Name == "fake_tool");

        (bool Enabled, ToolDefinition Definition) entry =
            registry.ListAllDefinitions().Single(e => e.Definition.Name == "fake_tool");
        Assert.True(entry.Enabled);

        ToolResult result = await registry.InvokeAsync(
            "fake_tool", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.False(result.IsError);
    }

    /// <summary>
    /// Disabling a tool on the system axis removes it from <see cref="IToolRegistry.ListDefinitions"/>,
    /// the same as the user axis.
    /// </summary>
    [Fact]
    public void DisabledToolBySystem_RemovesFromListDefinitions()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisabledToolBySystem("fake_tool");

        Assert.DoesNotContain(registry.ListDefinitions(), d => d.Name == "fake_tool");
    }

    /// <summary>
    /// Unlike the user axis, a system-disabled tool is absent from
    /// <see cref="IToolRegistry.ListAllDefinitions"/> <em>entirely</em> — it is filtered out before the
    /// enabled/disabled tuple is even built, rather than being reported with <c>Enabled == false</c>.
    /// </summary>
    [Fact]
    public void DisabledToolBySystem_IsAbsentFromListAllDefinitions_Entirely()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisabledToolBySystem("fake_tool");

        Assert.DoesNotContain(registry.ListAllDefinitions(), e => e.Definition.Name == "fake_tool");
    }

    /// <summary>
    /// <see cref="IToolRegistry.EnableToolBySystem"/> reverses both effects of
    /// <see cref="IToolRegistry.DisabledToolBySystem"/>: the tool reappears in both
    /// <see cref="IToolRegistry.ListDefinitions"/> and <see cref="IToolRegistry.ListAllDefinitions"/>.
    /// </summary>
    [Fact]
    public void EnableToolBySystem_RestoresListDefinitions_AndListAllDefinitions()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);
        registry.DisabledToolBySystem("fake_tool");

        registry.EnableToolBySystem("fake_tool");

        Assert.Contains(registry.ListDefinitions(), d => d.Name == "fake_tool");
        Assert.Contains(registry.ListAllDefinitions(), e => e.Definition.Name == "fake_tool");
    }

    /// <summary>
    /// A system-disabled tool is rejected by <see cref="IToolRegistry.InvokeAsync"/> with the same
    /// fixed error message as a user-disabled one — both axes feed the same disabled check.
    /// </summary>
    [Fact]
    public async Task DisabledToolBySystem_InvokeAsync_ReturnsDisabledError()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisabledToolBySystem("fake_tool");

        ToolResult result = await registry.InvokeAsync(
            "fake_tool", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Tool is disabled.", result.Content);
    }

    /// <summary>
    /// The two axes are tracked independently: disabling a tool on both axes and then re-enabling
    /// only the user axis leaves it disabled (system flag still set, so it stays entirely absent from
    /// <see cref="IToolRegistry.ListAllDefinitions"/>); re-enabling the system axis afterward is then
    /// sufficient to restore it, since the user axis was already cleared.
    /// </summary>
    [Fact]
    public void UserAndSystemAxes_AreIndependent()
    {
        var registry = new ToolRegistry([new FakeTool("fake_tool")]);

        registry.DisableToolByUser("fake_tool");
        registry.DisabledToolBySystem("fake_tool");

        registry.EnableToolByUser("fake_tool");

        // System axis still disables the tool, so it remains entirely absent, not merely
        // reported as Enabled == false.
        Assert.DoesNotContain(registry.ListAllDefinitions(), e => e.Definition.Name == "fake_tool");
        Assert.DoesNotContain(registry.ListDefinitions(), d => d.Name == "fake_tool");

        registry.EnableToolBySystem("fake_tool");

        // Both axes are now clear, so the tool is fully restored.
        Assert.Contains(registry.ListDefinitions(), d => d.Name == "fake_tool");
        (bool Enabled, ToolDefinition Definition) entry =
            registry.ListAllDefinitions().Single(e => e.Definition.Name == "fake_tool");
        Assert.True(entry.Enabled);
    }
}
