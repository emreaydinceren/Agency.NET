using Agency.Memory.Common.Records;
using Agency.Memory.Consolidator.Prompts;

namespace Agency.Memory.Consolidator.Test;

/// <summary>
/// Tests for <see cref="ConsolidatorReconciliationPrompt"/> per Spec §18.2.
/// </summary>
public class ConsolidatorReconciliationPromptTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Agency.Memory.Common.Records.Record MakeRecord(
        string id,
        string userId = "user1",
        ContentType contentType = ContentType.Fact,
        string domain = "Preferences",
        string key = "Language",
        string title = "Python preference",
        string value = "User prefers Python.",
        double importance = 0.7,
        string[]? tags = null,
        DateTimeOffset? updatedAt = null) =>
        Agency.Memory.Common.Records.Record.Create(
            id: id,
            userId: userId,
            sessionId: null,
            contentType: contentType,
            domain: domain,
            key: key,
            title: title,
            value: value,
            tags: tags ?? ["language", "python"],
            importance: importance,
            createdAt: _now.AddDays(-7),
            updatedAt: updatedAt ?? _now.AddDays(-3));

    /// <summary>
    /// The rendered prompt includes userId, maxIterations, fact threshold, memory threshold, and the records dump.
    /// </summary>
    [Fact]
    public void Render_IncludesUserId_RecordsDump_ThresholdHints_MaxIterations()
    {
        var records = new[]
        {
            MakeRecord("id-1"),
            MakeRecord("id-2", domain: "Debugging", key: "SslIssue", title: "SSL debugging episode",
                value: "Fixed SSL via DNS.", contentType: ContentType.Memory, tags: ["ssl", "dns"]),
        };

        string prompt = ConsolidatorReconciliationPrompt.Render(
            userId: "user1",
            records: records,
            maxIterations: 20,
            factThreshold: 0.85,
            memoryThreshold: 0.75);

        Assert.Contains("user1", prompt);
        Assert.Contains("20", prompt);
        Assert.Contains("0.85", prompt);
        Assert.Contains("0.75", prompt);
        Assert.Contains("id-1", prompt);
        Assert.Contains("id-2", prompt);
    }

    /// <summary>
    /// Each record in the dump includes id, ContentType, Domain, Key, Title, Tags, Importance, Age, and value preview.
    /// </summary>
    [Fact]
    public void Render_RecordsDump_FormatsEachRecordWithRequiredFields()
    {
        var records = new[] { MakeRecord("abc-123", title: "Python preference", importance: 0.7) };

        string prompt = ConsolidatorReconciliationPrompt.Render(
            userId: "user1",
            records: records,
            maxIterations: 20,
            factThreshold: 0.85,
            memoryThreshold: 0.75);

        // Id present
        Assert.Contains("abc-123", prompt);
        // ContentType present
        Assert.Contains("Fact", prompt);
        // Domain present
        Assert.Contains("Preferences", prompt);
        // Key present
        Assert.Contains("Language", prompt);
        // Title present
        Assert.Contains("Python preference", prompt);
        // Tags present
        Assert.Contains("language", prompt);
        Assert.Contains("python", prompt);
        // Importance present
        Assert.Contains("0.7", prompt);
        // Value preview present
        Assert.Contains("User prefers Python.", prompt);
    }

    /// <summary>
    /// The golden prompt output for a five-record fixture matches expected structure.
    /// </summary>
    [Fact]
    public void Golden_PromptOutput_ForFiveRecordsFixture()
    {
        var records = new[]
        {
            MakeRecord("r1", title: "Python preference", domain: "Preferences", key: "Language", importance: 0.7),
            MakeRecord("r2", title: "Postgres preference", domain: "Preferences", key: "Database",
                value: "User prefers Postgres.", tags: ["database", "postgres"], importance: 0.6),
            MakeRecord("r3", contentType: ContentType.Memory, domain: "Debugging", key: "SslIssue",
                title: "SSL debugging", value: "## Observation\nSSL error.\n## Action\nChecked DNS.\n## Outcome\nFixed.",
                tags: ["ssl", "dns"], importance: 0.8),
            MakeRecord("r4", title: "Vim keybindings", domain: "Preferences", key: "Editor",
                value: "User uses Vim keybindings.", tags: ["editor", "vim"], importance: 0.5),
            MakeRecord("r5", title: "Pytest usage", domain: "Preferences", key: "TestFramework",
                value: "User uses pytest.", tags: ["testing", "pytest"], importance: 0.6),
        };

        string prompt = ConsolidatorReconciliationPrompt.Render(
            userId: "user-golden",
            records: records,
            maxIterations: 20,
            factThreshold: 0.85,
            memoryThreshold: 0.75);

        // All five records are represented.
        foreach (string id in new[] { "r1", "r2", "r3", "r4", "r5" })
        {
            Assert.Contains(id, prompt);
        }

        // Prompt contains the Memory_Done instruction.
        Assert.Contains("Memory_Done", prompt);
        // Prompt contains decision categories.
        Assert.Contains("MERGE", prompt);
        Assert.Contains("DELETE", prompt);
        Assert.Contains("SKIP", prompt);
    }

    /// <summary>
    /// Prompt version constant is 3 (bumped for the same-Domain/Key merge priority rule).
    /// </summary>
    [Fact]
    public void Version_IsThree()
    {
        Assert.Equal(3, ConsolidatorReconciliationPrompt.Version);
    }

    /// <summary>
    /// The reconciliation prompt carries the explicit structural DELETE rule for clear-cut
    /// stale low-importance records (TI-8.4).
    /// </summary>
    [Fact]
    public void Render_IncludesStructuralDeleteRule()
    {
        string prompt = ConsolidatorReconciliationPrompt.Render(
            userId: "user1",
            records: [MakeRecord("id-1")],
            maxIterations: 20,
            factThreshold: 0.85,
            memoryThreshold: 0.75);

        Assert.Contains("Importance < 0.1", prompt);
        Assert.Contains("Age > 30 days", prompt);
    }
}
