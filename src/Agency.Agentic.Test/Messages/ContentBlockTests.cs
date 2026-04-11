using System.Text.Json;

namespace Agency.Agentic.Test.Messages;

/// <summary>
/// Unit tests for the <see cref="ContentBlock"/> discriminated union and its derived records.
/// </summary>
public sealed class ContentBlockTests
{
    // ── TextBlock ────────────────────────────────────────────────────────────

    [Fact]
    public void TextBlock_StoresText()
    {
        var block = new TextBlock("Hello, world!");

        Assert.Equal("Hello, world!", block.Text);
    }

    [Fact]
    public void TextBlock_StructuralEquality()
    {
        var a = new TextBlock("hello");
        var b = new TextBlock("hello");
        var c = new TextBlock("other");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── ToolUseBlock ─────────────────────────────────────────────────────────

    [Fact]
    public void ToolUseBlock_StoresFields()
    {
        var input = JsonDocument.Parse("""{"query":"foo"}""").RootElement;
        var block = new ToolUseBlock("use-1", "search", input);

        Assert.Equal("use-1", block.Id);
        Assert.Equal("search", block.Name);
        Assert.Equal("foo", block.Input.GetProperty("query").GetString());
    }

    // ── ToolResultBlock ───────────────────────────────────────────────────────

    [Fact]
    public void ToolResultBlock_DefaultsIsErrorToFalse()
    {
        var block = new ToolResultBlock("use-1", "some result");

        Assert.Equal("use-1", block.ToolUseId);
        Assert.Equal("some result", block.Content);
        Assert.False(block.IsError);
    }

    [Fact]
    public void ToolResultBlock_CanBeMarkedAsError()
    {
        var block = new ToolResultBlock("use-1", "Tool timeout", IsError: true);

        Assert.True(block.IsError);
    }

    // ── Pairing rule validation via LINQ ─────────────────────────────────────

    [Fact]
    public void OfType_ExtractsToolUseBlocks_FromMixedContent()
    {
        IReadOnlyList<ContentBlock> content =
        [
            new TextBlock("I will call a tool."),
            new ToolUseBlock("id-1", "tool_a", JsonDocument.Parse("{}").RootElement),
            new ToolUseBlock("id-2", "tool_b", JsonDocument.Parse("{}").RootElement),
        ];

        var toolUses = content.OfType<ToolUseBlock>().ToList();

        Assert.Equal(2, toolUses.Count);
        Assert.Equal("id-1", toolUses[0].Id);
        Assert.Equal("id-2", toolUses[1].Id);
    }

    [Fact]
    public void OfType_ReturnsEmpty_WhenNoToolUseBlocksPresent()
    {
        IReadOnlyList<ContentBlock> content = [new TextBlock("Plain text response.")];

        Assert.Empty(content.OfType<ToolUseBlock>());
    }

    // ── ThinkingBlock ─────────────────────────────────────────────────────────

    [Fact]
    public void ThinkingBlock_StoresThinking()
    {
        var block = new ThinkingBlock("Let me reason through this step by step.");

        Assert.Equal("Let me reason through this step by step.", block.Thinking);
    }

    // ── Pattern matching across the hierarchy ─────────────────────────────────

    [Fact]
    public void PatternMatch_CanDistinguishAllBlockTypes()
    {
        ContentBlock[] blocks =
        [
            new TextBlock("text"),
            new ToolUseBlock("id", "name", JsonDocument.Parse("{}").RootElement),
            new ToolResultBlock("id", "result"),
            new ThinkingBlock("thought"),
        ];

        var labels = blocks.Select(b => b switch
        {
            TextBlock t => $"text:{t.Text}",
            ToolUseBlock u => $"use:{u.Name}",
            ToolResultBlock r => $"result:{r.ToolUseId}",
            ThinkingBlock th => $"think:{th.Thinking}",
            _ => "unknown",
        }).ToArray();

        Assert.Equal(["text:text", "use:name", "result:id", "think:thought"], labels);
    }

    // ── AgentMessage ──────────────────────────────────────────────────────────

    [Fact]
    public void AgentMessage_StoresRoleAndContent()
    {
        var msg = new AgentMessage(MessageRole.Assistant, [new TextBlock("hello")]);

        Assert.Equal(MessageRole.Assistant, msg.Role);
        Assert.Single(msg.Content);
        Assert.IsType<TextBlock>(msg.Content[0]);
    }

    [Fact]
    public void AgentMessage_RoleEnum_CoversRequiredValues()
    {
        // Ensures the enum has at least the three values the loop depends on.
        Assert.True(Enum.IsDefined(typeof(MessageRole), MessageRole.System));
        Assert.True(Enum.IsDefined(typeof(MessageRole), MessageRole.User));
        Assert.True(Enum.IsDefined(typeof(MessageRole), MessageRole.Assistant));
    }
}
