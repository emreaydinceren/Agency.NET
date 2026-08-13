using System.Text.Json;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Tools;
using Moq;

namespace Agency.Memory.Distiller.Test.Tools;

/// <summary>
/// Tests for <see cref="MemorizeNowTool"/> (MemorizeNow-Design.md § Tool Signature, Console Output; UT-3).
/// </summary>
public sealed class MemorizeNowToolTests
{
    private const string ValidInput = """
        {
            "title": "Python 3.10 async perf",
            "value": "Python 3.10+ is 40% faster at async startup than 3.9.",
            "domain": "Performance",
            "importance": "High",
            "tags": ["async", "startup"]
        }
        """;

    private static MemorizeNowTool CreateTool(IMemoryStore store, string userId = "u1", string sessionId = "s1") =>
        new(store, userId, sessionId);

    private static Mock<IMemoryStore> CreateStrictStoreMock() => new(MockBehavior.Strict);

    // ── Delegation & success output ──────────────────────────────────────────

    /// <summary>Valid input delegates to IMemoryStore.MemorizeNowAsync with the exact agent-supplied
    /// parameters, and the tool returns a non-error confirmation containing the composite key.</summary>
    [Fact]
    public async Task Invoke_WithValidInput_DelegatesToStore_WithExactParams_AndReturnsConfirmation()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                "u1", "s1", "Python 3.10 async perf",
                "Python 3.10+ is 40% faster at async startup than 3.9.",
                "Performance", Importance.High,
                It.Is<string[]>(t => t.SequenceEqual(new[] { "async", "startup" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("performance|python-310-async-perf");

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse(ValidInput).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("✓ Memorized: performance|python-310-async-perf", result.Content);
        store.Verify(s => s.MemorizeNowAsync(
                "u1", "s1", "Python 3.10 async perf",
                "Python 3.10+ is 40% faster at async startup than 3.9.",
                "Performance", Importance.High,
                It.Is<string[]>(t => t.SequenceEqual(new[] { "async", "startup" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Empty-array tags (0 items) are valid and are passed through to the store unchanged.</summary>
    [Fact]
    public async Task Invoke_WithEmptyTagsArray_IsAccepted_Succeeds()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                "u1", "s1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Importance>(), It.Is<string[]>(t => t.Length == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync("performance|no-tags");

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"No tags fact","value":"Value text.","domain":"Performance","importance":"Normal","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("✓ Memorized: performance|no-tags", result.Content);
    }

    // ── Validation errors (store must NOT be called — Strict mock enforces this) ─

    /// <summary>An empty (whitespace) title is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_EmptyTitle_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"   ","value":"Value.","domain":"Performance","importance":"High","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("title", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A missing title property is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_MissingTitle_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"value":"Value.","domain":"Performance","importance":"High","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("title", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty value is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_EmptyValue_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"Title","value":"","domain":"Performance","importance":"High","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("value", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A missing domain property is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_MissingDomain_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"Title","value":"Value.","importance":"High","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("domain", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An importance value outside High/Normal/Low is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_InvalidImportance_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"Title","value":"Value.","domain":"Performance","importance":"Urgent","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("importance", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("High, Normal, or Low", result.Content);
    }

    /// <summary>A missing importance property is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_MissingImportance_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"Title","value":"Value.","domain":"Performance","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("importance", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>More than 4 tags is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_MoreThanFourTags_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"Title","value":"Value.","domain":"Performance","importance":"High",
             "tags":["a","b","c","d","e"]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("tags", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0-4", result.Content);
    }

    /// <summary>A missing tags property is rejected before the store is invoked.</summary>
    [Fact]
    public async Task Invoke_MissingTags_ReturnsError_AndDoesNotCallStore()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"Title","value":"Value.","domain":"Performance","importance":"High"}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("tags", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── Store-level validation errors propagate as tool errors ──────────────────

    /// <summary>An ArgumentException thrown by the store (its own re-validation) is caught and
    /// surfaced as an "Error: {message}" tool result rather than propagating.</summary>
    [Fact]
    public async Task Invoke_StoreThrowsArgumentException_ReturnsErrorWithStoreMessage()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Importance>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("domain cannot be null/empty"));

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse(ValidInput).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Error: domain cannot be null/empty", result.Content);
    }

    // ── Console output format ────────────────────────────────────────────────

    /// <summary>The confirmation always reports Source: AgentSignaled — MemorizeNow-persisted
    /// records are never any other provenance.</summary>
    [Fact]
    public async Task Invoke_ConfirmationMessage_IncludesSourceAgentSignaled()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Importance>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("performance|python-310-async-perf");

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse(ValidInput).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.Contains("Source: AgentSignaled", result.Content);
    }

    /// <summary>The confirmation echoes back the agent-supplied importance level.</summary>
    [Fact]
    public async Task Invoke_ConfirmationMessage_IncludesImportanceLevel()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Importance>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("performance|python-310-async-perf");

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse(ValidInput).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.Contains("Importance: High", result.Content);
    }

    /// <summary>When tags were supplied, the confirmation includes a "Tags: ..." line listing them.</summary>
    [Fact]
    public async Task Invoke_ConfirmationMessage_WithTags_IncludesTagsLine()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Importance>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("performance|python-310-async-perf");

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse(ValidInput).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.Contains("Tags: async, startup", result.Content);
    }

    /// <summary>When no tags were supplied, the confirmation omits the "Tags:" line entirely.</summary>
    [Fact]
    public async Task Invoke_ConfirmationMessage_WithoutTags_OmitsTagsLine()
    {
        Mock<IMemoryStore> store = CreateStrictStoreMock();
        store.Setup(s => s.MemorizeNowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Importance>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("performance|no-tags");

        MemorizeNowTool tool = CreateTool(store.Object);
        JsonElement input = JsonDocument.Parse("""
            {"title":"No tags fact","value":"Value text.","domain":"Performance","importance":"Normal","tags":[]}
            """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.DoesNotContain("Tags:", result.Content);
    }

    // ── Tool definition / description ────────────────────────────────────────

    /// <summary>The tool is registered under the exact, case-sensitive name "MemorizeNow".</summary>
    [Fact]
    public void Definition_Name_IsMemorizeNow()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);

        Assert.Equal("MemorizeNow", tool.Definition.Name);
    }

    /// <summary>The description guides the agent on when to call the tool, warns against saving
    /// secrets, and explains the three importance levels — so the agent can self-serve without
    /// external documentation.</summary>
    [Fact]
    public void Definition_Description_CoversUsageGuidance_SecretsWarning_AndImportanceLevels()
    {
        MemorizeNowTool tool = CreateTool(CreateStrictStoreMock().Object);
        string description = tool.Definition.Description;

        Assert.Contains("Use MemorizeNow when", description);
        Assert.Contains("Secrets, tokens, credentials", description);
        Assert.Contains("High (reshapes future decisions)", description);
        Assert.Contains("Normal (useful reference)", description);
        Assert.Contains("Low (edge case)", description);
    }
}
