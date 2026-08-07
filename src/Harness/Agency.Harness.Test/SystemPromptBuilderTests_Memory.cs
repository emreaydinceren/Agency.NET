using Agency.Harness.Contexts;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for the <c>## Facts</c> and <c>## Memories</c> sections that
/// <see cref="SystemPromptBuilder"/> renders when the retrieval engine has populated
/// <see cref="KnowledgeContext.Records"/> and <see cref="MemoryContext.Records"/> (D.3).
/// </summary>
public sealed class SystemPromptBuilderTests_Memory
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
    /// When recall produced records, the prompt must tell the model that those records are its own
    /// persisted recollection — otherwise models fall back on "I have no memory of past conversations"
    /// and disclaim away the records they were just handed (TS-MEM-02).
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Build_WithRecalledRecords_RendersMemoryPolicy(bool withFact, bool withMemory)
    {
        var ctx = MinimalContext();
        if (withFact)
        {
            ctx.Knowledge = ctx.Knowledge with { Records = [MakeFact()] };
        }

        if (withMemory)
        {
            ctx.Memory = ctx.Memory with { Records = [MakeMemory()] };
        }

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("## Memory", result, StringComparison.Ordinal);
        Assert.Contains("You are not stateless.", result, StringComparison.Ordinal);
        Assert.Contains("never claim you cannot remember previous conversations", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No relevant memories", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Memory attached but the store still empty — retrieval ran and returned nothing, so
    /// <see cref="Context.MemoryLastRetrievedAt"/> is stamped while both Record collections stay empty.
    /// The model must still be told memory persists: otherwise a cold start renders as nothing but
    /// "No relevant memories yet.", which reads as amnesia rather than as an empty store.
    /// </summary>
    [Fact]
    public void Build_MemoryRetrievedButEmpty_RendersColdStartPolicy()
    {
        var ctx = MinimalContext();
        ctx.MemoryLastRetrievedAt = DateTimeOffset.UtcNow;

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("## Memory", result, StringComparison.Ordinal);
        Assert.Contains("You are not stateless.", result, StringComparison.Ordinal);
        Assert.Contains("never take that as evidence", result, StringComparison.Ordinal);
        // The recall-specific wording would be a lie here — there is nothing below to point at.
        Assert.DoesNotContain("is your own recollection", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/memory</c> switches memory off mid-session. The Context outlives the turn, so the stamp and
    /// the prior turn's records survive the toggle — the policy must not keep promising continuity for
    /// a session the user just opted out of.
    /// </summary>
    [Fact]
    public void Build_MemoryToggledOff_OmitsMemoryPolicy_EvenWithStaleRecords()
    {
        var ctx = MinimalContext();
        ctx.Knowledge = ctx.Knowledge with { Records = [MakeFact()] };
        ctx.MemoryLastRetrievedAt = DateTimeOffset.UtcNow;
        ctx.MemoryEnabled = false;

        string result = SystemPromptBuilder.Build(ctx);

        Assert.DoesNotContain("## Memory", result, StringComparison.Ordinal);
        Assert.DoesNotContain("You are not stateless.", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The policy claims continuity ("you are not stateless"), so it must stay absent when no memory
    /// system is attached at all — no records and retrieval never ran.
    /// </summary>
    [Fact]
    public void Build_WithoutRecalledRecords_OmitsMemoryPolicy()
    {
        var ctx = MinimalContext();
        // Host-injected facts are not retrieval output, so they must not trigger the policy.
        ctx.Knowledge = ctx.Knowledge with { Facts = ["The sky is blue."] };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.DoesNotContain("## Memory", result, StringComparison.Ordinal);
        Assert.DoesNotContain("You are not stateless.", result, StringComparison.Ordinal);
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
