using Agency.Harness.Contexts;
using Agency.Harness.Memory;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for the <c>&lt;memory&gt;</c> block that <see cref="MemoryRenderer"/> renders when the
/// retrieval engine has populated <see cref="KnowledgeContext.Records"/> and
/// <see cref="MemoryContext.Records"/> (D.3). These sections used to live in the system prompt;
/// they now render into a user message adjacent to the current turn.
/// </summary>
public sealed class MemoryRendererTests
{
    /// <summary>
    /// Mirrors production: <see cref="ChatSession"/> seeds every new context with memory enabled,
    /// so tests that do not exercise the <c>/memory</c> toggle must start from that state.
    /// </summary>
    private static Context MinimalContext() =>
        new()
        {
            Query = new QueryContext { Prompt = "Test prompt" },
            MemoryEnabled = true,
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
    /// When <see cref="KnowledgeContext.Records"/> contains facts, the block must include
    /// a <c>## Facts from Memory</c> section with each record's title and value, and a
    /// human-readable recency hint (not a raw timestamp).
    /// </summary>
    [Fact]
    public void Build_WithFacts_RendersFactsSection_WithRecencyHint_NotRawTimestamp()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with
        {
            Records = [MakeFact(ageMinutes: 3)],
        };

        string result = MemoryRenderer.Build(ctx);

        Assert.Contains("## Facts from Memory", result);
        Assert.Contains("Python preference", result);
        Assert.Contains("User prefers Python.", result);
        // Should contain a human-readable recency hint
        Assert.Contains("ago", result, StringComparison.OrdinalIgnoreCase);
        // Must NOT contain a raw ISO timestamp
        Assert.DoesNotContain("T00:", result);
        Assert.DoesNotContain("+00:00", result);
    }

    /// <summary>
    /// The rendered block must be wrapped in <c>&lt;memory&gt;</c> tags, mirroring the
    /// <c>&lt;project-instructions&gt;</c> block, so the model can tell injected context from
    /// something the user typed.
    /// </summary>
    [Fact]
    public void Build_WithFacts_WrapsBlockInMemoryTags()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with { Records = [MakeFact()] };

        string result = MemoryRenderer.Build(ctx);

        Assert.StartsWith("<memory>", result, StringComparison.Ordinal);
        Assert.EndsWith("</memory>" + Environment.NewLine, result, StringComparison.Ordinal);
    }

    /// <summary>
    /// When <see cref="MemoryContext.Records"/> contains memories, the block must include
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

        string result = MemoryRenderer.Build(ctx);

        Assert.Contains("## Memories", result);
        Assert.Contains("SSL Debugging", result);
        Assert.Contains("## Observation", result);
        Assert.Contains("Had SSL issues.", result);
        Assert.Contains("ago", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The memory operating policy renders under its own <c>## Memory Policy</c> heading and
    /// precedes the records it introduces.
    /// </summary>
    [Fact]
    public void Build_WithMemoryPolicy_RendersPolicyBeforeRecords()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with
        {
            MemoryPolicy = "Save as you learn: call MemorizeNow.",
            Records = [MakeFact()],
        };

        string result = MemoryRenderer.Build(ctx);

        Assert.Contains("## Memory Policy", result);
        Assert.Contains("Save as you learn: call MemorizeNow.", result);
        Assert.True(
            result.IndexOf("## Memory Policy", StringComparison.Ordinal)
                < result.IndexOf("## Facts from Memory", StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>## Facts from Memory</c> heading must carry an instruction to apply the records to
    /// the current request. Without one the only directive to use recall sits inside the policy,
    /// far enough upstream that a small model reads the records as background, not as answers.
    /// </summary>
    [Fact]
    public void Build_WithFacts_CarriesApplyDirective()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with { Records = [MakeFact()] };

        string result = MemoryRenderer.Build(ctx);

        Assert.Contains("apply them to the current request", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With a policy present but nothing recalled, the block must say so explicitly
    /// (Spec §13 — "No relevant memories yet."), because silence reads as "I have no memory".
    /// </summary>
    [Fact]
    public void Build_PolicyButNoRecords_RendersNoRelevantMemoriesNote()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with { MemoryPolicy = "Some policy." };

        string result = MemoryRenderer.Build(ctx);

        Assert.Contains("No relevant memories", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With no policy and nothing recalled — memory disabled — the renderer returns empty so the
    /// agent loop injects no message at all, rather than asserting an absence unprompted.
    /// </summary>
    [Fact]
    public void Build_NoPolicyAndNoRecords_ReturnsEmpty()
    {
        var ctx = MinimalContext();

        string result = MemoryRenderer.Build(ctx);

        Assert.Empty(result);
    }

    /// <summary>
    /// The block must never include the raw numeric composite score or the raw embedding
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

        string result = MemoryRenderer.Build(ctx);

        // No raw float array patterns like [0.1, 0.2, 0.3]
        Assert.DoesNotContain("[0.", result);
        // No numeric score labels
        Assert.DoesNotContain("score:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("similarity:", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recall no longer renders into the system prompt. This is the regression guard for the move:
    /// leaving a copy behind would double every recalled record in the request.
    /// </summary>
    [Fact]
    public void SystemPrompt_NoLongerCarriesRecalledRecords()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with
        {
            MemoryPolicy = "Some policy.",
            Records = [MakeFact()],
        };
        ctx.Memory = ctx.Memory with { Records = [MakeMemory()] };

        string systemPrompt = SystemPromptBuilder.Build(ctx);

        Assert.DoesNotContain("## Facts from Memory", systemPrompt);
        Assert.DoesNotContain("## Memories", systemPrompt);
        Assert.DoesNotContain("## Memory Policy", systemPrompt);
        Assert.DoesNotContain("Python preference", systemPrompt);
    }
}
