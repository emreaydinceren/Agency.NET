using System.Text.Json;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for <see cref="ProgressiveDiscoveryToolRegistry"/>. Progressive disclosure is applied by tool
/// <em>origin</em>: MCP tools (names passed at construction) have their schema withheld and description
/// summarized; native/internal tools are revealed in full.
/// </summary>
public sealed class ProgressiveDiscoveryToolRegistryTests
{
    /// <summary>Decorates <paramref name="inner"/>, treating <paramref name="mcpToolNames"/> as MCP-originated.</summary>
    private static ProgressiveDiscoveryToolRegistry Decorate(ToolRegistry inner, params string[] mcpToolNames) =>
        new(inner, new HashSet<string>(mcpToolNames));

    private static ProgressiveDiscoveryToolRegistry BuildDecorator(out ToolRegistry inner)
    {
        inner = new ToolRegistry();
        inner.Register(new ReadFileTool());
        inner.Register(new FakeTool("fake_beta"));
        // No MCP tools — read_file and fake_beta are native and revealed in full.
        return Decorate(inner);
    }

    [Fact]
    public void ListDefinitions_ReturnsInnerCountPlusOne()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out ToolRegistry inner);
        int innerCount = inner.ListDefinitions().Count;

        IReadOnlyList<ToolDefinition> defs = decorator.ListDefinitions();

        Assert.Equal(innerCount + 1, defs.Count);
    }

    [Fact]
    public void ListDefinitions_IncludesOriginalToolsByName()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out _);

        IReadOnlyList<ToolDefinition> defs = decorator.ListDefinitions();
        IEnumerable<string> names = defs.Select(static d => d.Name);

        Assert.Contains("read_file", names);
        Assert.Contains("fake_beta", names);
    }

    [Fact]
    public void ListDefinitions_NativeTool_KeepsFullSchemaAndDescription()
    {
        var inner = new ToolRegistry();
        // A native tool with a multi-line description and a parameter carrying description + enum —
        // exactly the detail that is withheld for MCP tools but must survive for native ones.
        inner.Register(new FakeTool("native_tool", description: "Does a thing\nLine two of detail.", schema: """
            {"type":"object","properties":{"mode":{"type":"string","description":"how to run","enum":["fast","slow"]}},"required":["mode"]}
            """));
        var decorator = Decorate(inner);   // not an MCP tool

        ToolDefinition def = decorator.ListDefinitions().First(static d => d.Name == "native_tool");
        JsonElement mode = def.InputSchema.GetProperty("properties").GetProperty("mode");

        // Full schema preserved: param description and enum are kept.
        Assert.Equal("string", mode.GetProperty("type").GetString());
        Assert.Equal("how to run", mode.GetProperty("description").GetString());
        Assert.True(mode.TryGetProperty("enum", out _));
        // Description is NOT summarized for native tools.
        Assert.Equal("Does a thing\nLine two of detail.", def.Description);
    }

    [Fact]
    public void ListDefinitions_McpTool_WithholdsSchema_AndSummarizesDescription()
    {
        var inner = new ToolRegistry();
        inner.Register(new FakeTool("API_get_user",
            description: "Notion | Retrieve a user\nError Responses:\n400: Bad request",
            schema: """
            {"type":"object","properties":{"user_id":{"type":"string"}},"required":["user_id"]}
            """));
        var decorator = Decorate(inner, "API_get_user");   // MCP tool

        ToolDefinition def = decorator.ListDefinitions().First(static d => d.Name == "API_get_user");

        // Schema collapses to the bare {"type":"object"} placeholder — no properties advertised.
        Assert.Equal("object", def.InputSchema.GetProperty("type").GetString());
        Assert.False(def.InputSchema.TryGetProperty("properties", out _),
            "MCP tool schema must be withheld entirely.");
        Assert.False(def.InputSchema.TryGetProperty("required", out _));
        // Description reduced to its first line (vendor prefix preserved), then the inline tool_help
        // directive is appended so the withheld schema is never called blind.
        Assert.StartsWith("Notion | Retrieve a user", def.Description);
        Assert.Contains("tool_help(name: \"API_get_user\")", def.Description);
    }

    [Fact]
    public void ListDefinitions_McpTool_SummarizePreservesVendorPrefix()
    {
        var inner = new ToolRegistry();
        inner.Register(new FakeTool("api_thing", description:
            "Notion | Update a page's content as Markdown\nError Responses:\n400: Bad request\n429: Rate limited."));
        var decorator = Decorate(inner, "api_thing");

        ToolDefinition def = decorator.ListDefinitions().First(static d => d.Name == "api_thing");

        // The "Notion | " prefix is the model's only inline cue to which server a tool belongs
        // (operationId-derived names like "api_thing" carry none), so it must survive summarization.
        Assert.StartsWith("Notion | Update a page's content as Markdown", def.Description);
    }

    [Fact]
    public void ListDefinitions_McpTool_AppendsInlineToolHelpDirectiveNamingTheTool()
    {
        var inner = new ToolRegistry();
        inner.Register(new FakeTool("API_get_user", description: "Notion | Retrieve a user", schema: """
            {"type":"object","properties":{"user_id":{"type":"string"}},"required":["user_id"]}
            """));
        var decorator = Decorate(inner, "API_get_user");   // MCP tool — schema withheld

        ToolDefinition def = decorator.ListDefinitions().First(static d => d.Name == "API_get_user");

        // The directive must name THIS tool so the model knows exactly what to pass to tool_help.
        Assert.Contains("tool_help(name: \"API_get_user\")", def.Description);
        Assert.Contains("Schema withheld", def.Description);
    }

    [Fact]
    public void ListDefinitions_NativeTool_HasNoToolHelpDirective()
    {
        var inner = new ToolRegistry();
        inner.Register(new FakeTool("native_tool", description: "Does a thing", schema: """
            {"type":"object","properties":{"mode":{"type":"string"}},"required":["mode"]}
            """));
        var decorator = Decorate(inner);   // not an MCP tool — schema revealed in full

        ToolDefinition def = decorator.ListDefinitions().First(static d => d.Name == "native_tool");

        // Native tools advertise their full schema, so there is nothing to fetch via tool_help.
        Assert.DoesNotContain("tool_help", def.Description);
    }

    [Fact]
    public void ListDefinitions_ToolHelpDefinitionHasFullSchema()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out _);

        IReadOnlyList<ToolDefinition> defs = decorator.ListDefinitions();
        ToolDefinition helpDef = defs.First(static d => d.Name == "tool_help");

        // tool_help's own schema must have a "name" property — it is NOT stripped.
        Assert.True(helpDef.InputSchema.TryGetProperty("properties", out JsonElement props));
        Assert.True(props.TryGetProperty("name", out _));
    }

    [Fact]
    public void ListDefinitions_ToolHelpIsLast()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out _);

        IReadOnlyList<ToolDefinition> defs = decorator.ListDefinitions();

        Assert.Equal("tool_help", defs[^1].Name);
    }

    [Fact]
    public async Task InvokeAsync_RealTool_DelegatesToInner()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out _);

        string tempFile = Path.GetTempFileName();
        string expectedContent = $"hello-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(tempFile, expectedContent, CancellationToken.None);

        try
        {
            ToolResult result = await decorator.InvokeAsync(
                "read_file",
                JsonSerializer.SerializeToElement(new { path = tempFile }),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains(expectedContent, result.Content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task InvokeAsync_ToolHelp_ReturnsFullSchemaText()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out _);

        ToolResult result = await decorator.InvokeAsync(
            "tool_help",
            JsonSerializer.SerializeToElement(new { name = "read_file" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("path", result.Content);
        Assert.Contains("Reads the contents of a file", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_ToolHelp_McpTool_ReturnsFullDescription_AfterSummarization()
    {
        var inner = new ToolRegistry();
        inner.Register(new FakeTool("api_thing", description:
            "Notion | Update a page's content as Markdown\nError Responses:\n400: Bad request"));
        var decorator = Decorate(inner, "api_thing");

        ToolResult result = await decorator.InvokeAsync(
            "tool_help",
            JsonSerializer.SerializeToElement(new { name = "api_thing" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        // tool_help reveals the FULL multi-line description, not the shortened catalog summary.
        Assert.Contains("Error Responses", result.Content);
    }

    [Fact]
    public void Register_DelegatesToInner_AndIsRevealedAsNative()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out ToolRegistry inner);
        var newTool = new FakeTool("late_registered", schema: """
            {"type":"object","properties":{"foo":{"type":"string"}}}
            """);

        decorator.Register(newTool);

        // Appears in the inner registry.
        Assert.Contains(inner.ListDefinitions(), static d => d.Name == "late_registered");

        // Appears in the decorator's own ListDefinitions; as a non-MCP tool it is revealed in full.
        IReadOnlyList<ToolDefinition> decoratorDefs = decorator.ListDefinitions();
        ToolDefinition? lateDef = decoratorDefs.FirstOrDefault(static d => d.Name == "late_registered");
        Assert.NotNull(lateDef);
        Assert.True(lateDef!.InputSchema.GetProperty("properties").TryGetProperty("foo", out _),
            "Late-registered native tool schema must be revealed in full.");
    }

    private const string CommandSchema =
        """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""";

    private static FakeTool NeedsCommandTool() => new(
        "needs_command",
        description: "Runs a command",
        schema: CommandSchema,
        handler: static input =>
            input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("command", out JsonElement c)
                && c.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(c.GetString())
                ? new ToolResult("ran")
                : new ToolResult("Missing required 'command'.", IsError: true));

    [Fact]
    public async Task InvokeAsync_MissingRequiredArg_AppendsFullSchemaToError()
    {
        var inner = new ToolRegistry();
        inner.Register(NeedsCommandTool());
        // An MCP tool whose schema is withheld — the case the self-heal exists for.
        var decorator = Decorate(inner, "needs_command");

        // The exact failing shape from the bug report: an empty object with no 'command' key.
        ToolResult result = await decorator.InvokeAsync(
            "needs_command",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        Assert.True(result.IsError);
        // The tool's own error is preserved...
        Assert.Contains("Missing required 'command'", result.Content);
        // ...and the full schema + description are folded in so the retry knows the shape.
        Assert.Contains("Runs a command", result.Content);
        Assert.Contains("required", result.Content);
        Assert.Contains("\"command\"", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_ValidArgs_SuccessfulResultNotAltered()
    {
        var inner = new ToolRegistry();
        inner.Register(NeedsCommandTool());
        var decorator = Decorate(inner, "needs_command");

        ToolResult result = await decorator.InvokeAsync(
            "needs_command",
            JsonSerializer.SerializeToElement(new { command = "tasklist" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ran", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_RequiredArgsPresentButToolErrors_NotAltered()
    {
        var inner = new ToolRegistry();
        // Required 'command' IS supplied, but the tool fails for an unrelated runtime reason. The
        // schema must NOT be appended — this is not a wrong-shape call.
        inner.Register(new FakeTool(
            "always_fails",
            schema: CommandSchema,
            handler: static _ => new ToolResult("runtime boom", IsError: true)));
        var decorator = Decorate(inner, "always_fails");

        ToolResult result = await decorator.InvokeAsync(
            "always_fails",
            JsonSerializer.SerializeToElement(new { command = "x" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("runtime boom", result.Content);
        Assert.DoesNotContain("properties", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_ExecutePowershell_EmptyArgs_SelfHealsWithCommandSchema()
    {
        // End-to-end against the real tool from the bug report.
        var inner = new ToolRegistry();
        inner.Register(new ExecutePowershellTool());
        var decorator = Decorate(inner, "execute_powershell");

        // Empty object — the shape that yielded "Received: []" (no keys) in the bug report.
        ToolResult result = await decorator.InvokeAsync(
            "execute_powershell",
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Missing required 'command'", result.Content);
        Assert.Contains("\"command\"", result.Content);
    }

    [Fact]
    public void ImplementsIProgressiveDiscovery()
    {
        ProgressiveDiscoveryToolRegistry decorator = BuildDecorator(out _);
        Assert.IsAssignableFrom<IProgressiveDiscovery>(decorator);
    }
}
