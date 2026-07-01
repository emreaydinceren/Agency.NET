using System.Text.Json;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for <see cref="ToolHelpTool"/>.
/// </summary>
public sealed class ToolHelpToolTests
{
    private static ToolRegistry BuildInnerRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new FakeTool("fake_alpha"));
        return registry;
    }

    /// <summary>
    /// The tool's <c>Definition</c> exposes the <c>tool_help</c> name, a description mentioning
    /// the full parameter schema, and an input schema requiring a string <c>name</c> property.
    /// </summary>
    [Fact]
    public void Definition_HasExpectedNameAndSchema()
    {
        var help = new ToolHelpTool(new ToolRegistry());

        Assert.Equal("tool_help", help.Definition.Name);
        Assert.Contains("full parameter schema", help.Definition.Description);

        JsonElement schema = help.Definition.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("name", schema.GetProperty("required")[0].GetString());
    }

    /// <summary>
    /// Requesting help for a tool that exists in the inner registry returns its full description
    /// and parameter schema.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_KnownTool_ReturnsDescriptionAndSchema()
    {
        ToolRegistry inner = BuildInnerRegistry();
        var help = new ToolHelpTool(inner);

        ToolResult result = await help.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "read_file" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Reads the contents of a file", result.Content);
        Assert.Contains("path", result.Content);
    }

    /// <summary>
    /// Requesting help for a name that is not registered returns an error that echoes the
    /// requested name and lists the names that are actually available.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnknownTool_ReturnsErrorWithAvailableNames()
    {
        ToolRegistry inner = BuildInnerRegistry();
        var help = new ToolHelpTool(inner);

        ToolResult result = await help.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "no_such_tool" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("no_such_tool", result.Content);
        Assert.Contains("read_file", result.Content);
        Assert.Contains("fake_alpha", result.Content);
    }

    /// <summary>
    /// Invoking with an arguments object that has no <c>name</c> property returns an error
    /// stating that <c>name</c> is required.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_MissingNameProperty_ReturnsError()
    {
        var help = new ToolHelpTool(new ToolRegistry());

        ToolResult result = await help.InvokeAsync(
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'name' is required", result.Content);
    }

    /// <summary>
    /// Invoking with an empty string <c>name</c> is treated the same as a missing name and
    /// returns the "name is required" error.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_EmptyNameString_ReturnsError()
    {
        var help = new ToolHelpTool(new ToolRegistry());

        ToolResult result = await help.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'name' is required", result.Content);
    }
}
