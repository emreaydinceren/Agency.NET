using Agency.Agentic.Contexts;

namespace Agency.Agentic.Test;

/// <summary>
/// Tests for the <c>## Facts</c> and <c>## Memories</c> sections that
/// <see cref="SystemPromptBuilder"/> renders when the retrieval engine has populated
/// <see cref="KnowledgeContext.Records"/> and <see cref="MemoryContext.Records"/> (D.3).
/// </summary>
public sealed class SystemPromptBuilderTests_Memory
{
    private static Context MinimalContext() =>
        new()
        {
            Query = new QueryContext { Prompt = "Test prompt" },
        };

    private static MemoryRecord MakeFact(
        string title = "Python preference",
        string value = "User prefers Python.",
        int ageMinutes = 5)
    {
        return new MemoryRecord(title, value, DateTimeOffset.UtcNow.AddMinutes(-ageMinutes));
    }

    private static MemoryRecord MakeMemory(
        string title = "SSL Debugging",
        string value = "## Observation\nHad SSL issues.\n## Action\nChecked DNS.",
        int ageDays = 3)
    {
        return new MemoryRecord(title, value, DateTimeOffset.UtcNow.AddDays(-ageDays));
    }

    /// <summary>
    /// When <see cref="KnowledgeContext.Records"/> contains facts, the prompt must include
    /// a <c>## Facts</c> section with each record's title and value, and a human-readable
    /// recency hint (not a raw timestamp).
    /// </summary>
    [Fact]
    public void Build_WithFacts_RendersFactsSection_WithRecencyHint_NotRawTimestamp()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with
        {
            Records = [MakeFact(ageMinutes: 3)],
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("## Facts", result);
        Assert.Contains("Python preference", result);
        Assert.Contains("User prefers Python.", result);
        // Should contain a human-readable recency hint
        Assert.Contains("ago", result, StringComparison.OrdinalIgnoreCase);
        // Must NOT contain a raw ISO timestamp
        Assert.DoesNotContain("T00:", result);
        Assert.DoesNotContain("+00:00", result);
    }

    /// <summary>
    /// When <see cref="MemoryContext.Records"/> contains memories, the prompt must include
    /// a <c>## Memories</c> section with each record's title, value, and a recency hint.
    /// Markdown in the OAO body must be preserved verbatim.
    /// </summary>
    [Fact]
    public void Build_WithMemories_RendersMemoriesSection_OaoMarkdownPreserved()
    {
        var ctx = MinimalContext();
        ctx.Memory = ctx.Memory with
        {
            Records = [MakeMemory(ageDays: 3)],
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("## Memories", result);
        Assert.Contains("SSL Debugging", result);
        Assert.Contains("## Observation", result);
        Assert.Contains("Had SSL issues.", result);
        Assert.Contains("## Action", result);
        Assert.Contains("ago", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When both <see cref="KnowledgeContext.Records"/> and <see cref="MemoryContext.Records"/>
    /// are empty, the builder must emit a note indicating no relevant memories are available,
    /// to inform the LLM explicitly (Spec §13 — "No relevant memories yet.").
    /// </summary>
    [Fact]
    public void Build_EmptyKnowledgeAndMemory_RendersNoRelevantMemoriesNote()
    {
        var ctx = MinimalContext();
        // Records are empty by default.

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("No relevant memories", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The prompt must never include the raw numeric composite score or the raw embedding
    /// vector. The LLM only sees human-readable recency.
    /// </summary>
    [Fact]
    public void Build_NeverIncludesRawScoreOrEmbedding()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with
        {
            Records = [MakeFact()],
        };

        string result = SystemPromptBuilder.Build(ctx);

        // No raw float array patterns like [0.1, 0.2, 0.3]
        Assert.DoesNotContain("[0.", result);
        // No numeric score labels
        Assert.DoesNotContain("score:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("similarity:", result, StringComparison.OrdinalIgnoreCase);
    }
}
