using Agency.Agentic.Contexts;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Records;
using Agency.Memory.Distiller.Prompts;
using Microsoft.Extensions.AI;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Tests for <see cref="EpisodeExtractionPrompt"/> rendering and
/// <see cref="EpisodeExtractionParser"/> parsing (Spec §18.1, §8.2).
/// </summary>
public sealed class EpisodeExtractionPromptTests
{
    private static DistillationJob MakeJob(
        DistillationTrigger trigger = DistillationTrigger.Inactivity,
        string? summary = null) =>
        new(UserId: "u1", SessionId: "s1", Trigger: trigger, UpToTurnIndex: 5, TriggerSummary: summary);

    private static IReadOnlyList<ChatMessage> MakeTurns() =>
    [
        new ChatMessage(ChatRole.User, "I prefer Python."),
        new ChatMessage(ChatRole.Assistant, "Got it. I'll keep that in mind."),
    ];

    // ── Rendering ──────────────────────────────────────────────────────────────

    /// <summary>Verifies trigger name, focus fields, known domains, and recent facts appear in the rendered prompt.</summary>
    [Fact]
    public void Render_IncludesTriggerName_FocusFields_KnownDomains_RecentFactsDump()
    {
        DistillationJob job = MakeJob(DistillationTrigger.GoalCompletion, "SSL issue resolved");
        FocusContext focus = new() { Title = "Auth Debugging", Domain = "Debugging", Tags = ["ssl", "dns"] };
        string[] domains = ["Preferences", "Debugging"];
        MemoryRecord[] facts = [MakeRecord("Python preference", "User prefers Python.")];

        string rendered = EpisodeExtractionPrompt.Render(job, MakeTurns(), focus, domains, facts);

        Assert.Contains("GoalCompletion", rendered);
        Assert.Contains("SSL issue resolved", rendered);
        Assert.Contains("Auth Debugging", rendered);
        Assert.Contains("Debugging", rendered);
        Assert.Contains("ssl", rendered);
        Assert.Contains("Preferences", rendered);
        Assert.Contains("Python preference", rendered);
        Assert.Contains("User prefers Python.", rendered);
    }

    /// <summary>Verifies that the trigger summary line is omitted when TriggerSummary is null.</summary>
    [Fact]
    public void Render_OmitsTriggerSummary_WhenNull()
    {
        DistillationJob job = MakeJob(summary: null);
        string rendered = EpisodeExtractionPrompt.Render(job, MakeTurns(), FocusContext.Empty, [], []);

        Assert.DoesNotContain("Trigger summary", rendered);
    }

    /// <summary>Verifies that the trigger summary appears when provided.</summary>
    [Fact]
    public void Render_IncludesTriggerSummary_WhenProvided()
    {
        DistillationJob job = MakeJob(summary: "Goal achieved: debug session finished");
        string rendered = EpisodeExtractionPrompt.Render(job, MakeTurns(), FocusContext.Empty, [], []);

        Assert.Contains("Goal achieved: debug session finished", rendered);
    }

    /// <summary>Verifies that the prompt version constant is 1 (Spec §18.5).</summary>
    [Fact]
    public void Version_Is1()
    {
        Assert.Equal(1, EpisodeExtractionPrompt.Version);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    /// <summary>Verifies that code fences are stripped before JSON parsing.</summary>
    [Fact]
    public void Parse_StripsCodeFences_BeforeJson()
    {
        const string json = """
            ```json
            {"records":[{"ContentType":"Fact","Title":"Python preference","Domain":"Preferences","Key":"Language","Tags":["python"],"Scope":"Global","Importance":0.7,"Value":"User prefers Python."}]}
            ```
            """;

        IReadOnlyList<MemoryRecord> records = EpisodeExtractionParser.Parse(json, "u1", "s1");

        Assert.Single(records);
        Assert.Equal("Python preference", records[0].Title);
    }

    /// <summary>Verifies that a valid JSON payload produces correctly mapped Record instances.</summary>
    [Fact]
    public void Parse_ValidPayload_ReturnsRecords()
    {
        const string json = """{"records":[{"ContentType":"Fact","Title":"Python preference","Domain":"Preferences","Key":"Language","Tags":["python"],"Scope":"Global","Importance":0.7,"Value":"User prefers Python."}]}""";

        IReadOnlyList<MemoryRecord> records = EpisodeExtractionParser.Parse(json, "u1", "s1");

        Assert.Single(records);
        MemoryRecord r = records[0];
        Assert.Equal(ContentType.Fact, r.ContentType);
        Assert.Equal("Python preference", r.Title);
        Assert.Equal("Preferences", r.Domain);
        Assert.Equal("Language", r.Key);
        Assert.Equal(["python"], r.Tags);
        Assert.Equal(0.7, r.Importance, 3);
        Assert.Equal("User prefers Python.", r.Value);
        Assert.Equal("u1", r.UserId);
        Assert.Null(r.SessionId); // Global scope → no session id
        Assert.NotEmpty(r.Id);
    }

    /// <summary>Verifies that Session-scoped records have the sessionId stamped.</summary>
    [Fact]
    public void Parse_SessionScope_StampsSessionId()
    {
        const string json = """{"records":[{"ContentType":"Memory","Title":"SSL debugging","Domain":"Debugging","Key":"SslDebug2026","Tags":[],"Scope":"Session","Importance":0.8,"Value":"## Observation\nSSL issue.\n## Action\nChecked DNS.\n## Outcome\nResolved.\n## Lesson\nCheck DNS first."}]}""";

        IReadOnlyList<MemoryRecord> records = EpisodeExtractionParser.Parse(json, "u1", "my-session");

        Assert.Equal("my-session", records[0].SessionId);
    }

    /// <summary>Verifies that invalid JSON throws ExtractionParseException.</summary>
    [Fact]
    public void Parse_InvalidJson_ThrowsExtractionParseException()
    {
        const string notJson = "this is not json";

        Assert.Throws<ExtractionParseException>(() =>
            EpisodeExtractionParser.Parse(notJson, "u1", "s1"));
    }

    /// <summary>Verifies that an empty records array is valid and returns zero records (Spec §18.1).</summary>
    [Fact]
    public void Parse_EmptyRecordsArray_IsValid_ReturnsZeroRecords()
    {
        const string json = """{"records":[]}""";

        IReadOnlyList<MemoryRecord> records = EpisodeExtractionParser.Parse(json, "u1", "s1");

        Assert.Empty(records);
    }

    /// <summary>Verifies that a record missing ContentType throws ExtractionParseException.</summary>
    [Fact]
    public void Parse_MissingRequiredField_ContentType_Throws()
    {
        const string json = """{"records":[{"Title":"X","Domain":"D","Key":"K","Value":"V","Importance":0.5}]}""";

        Assert.Throws<ExtractionParseException>(() =>
            EpisodeExtractionParser.Parse(json, "u1", "s1"));
    }

    /// <summary>Verifies that an unknown ContentType value throws ExtractionParseException.</summary>
    [Fact]
    public void Parse_UnknownContentType_Throws()
    {
        const string json = """{"records":[{"ContentType":"Unknown","Title":"X","Domain":"D","Key":"K","Value":"V","Importance":0.5}]}""";

        Assert.Throws<ExtractionParseException>(() =>
            EpisodeExtractionParser.Parse(json, "u1", "s1"));
    }

    // ── Goldens (Spec §18.5) ───────────────────────────────────────────────────

    /// <summary>Golden test: fact extraction prompt from a Python preference transcript.</summary>
    [Fact]
    public void Golden_FactExtraction_FromPythonPreferenceTranscript()
    {
        DistillationJob job = MakeJob(DistillationTrigger.Inactivity);
        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "I prefer Python for all my scripting work."),
            new(ChatRole.Assistant, "Got it. I'll use Python going forward."),
        };

        string rendered = EpisodeExtractionPrompt.Render(job, turns, FocusContext.Empty, [], []);

        string goldenPath = Path.Combine(
            AppContext.BaseDirectory,
            "Goldens", "FactExtraction_PythonPreference.txt");

        string goldenContent = File.ReadAllText(goldenPath)
            .Replace("\r\n", "\n")
            .Trim();

        string actual = rendered.Replace("\r\n", "\n").Trim();
        Assert.Equal(goldenContent, actual);
    }

    /// <summary>Golden test: memory extraction prompt from an SSL debugging transcript.</summary>
    [Fact]
    public void Golden_MemoryExtraction_FromSslDebuggingTranscript()
    {
        DistillationJob job = new(
            UserId: "u1",
            SessionId: "s1",
            Trigger: DistillationTrigger.GoalCompletion,
            UpToTurnIndex: 4,
            TriggerSummary: "SSL debugging session resolved via DNS");

        FocusContext focus = new() { Title = "SSL Debugging", Domain = "Debugging", Tags = ["ssl", "dns"] };

        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "My SSL handshake keeps failing."),
            new(ChatRole.Assistant, "Let me check the certificate chain."),
            new(ChatRole.User, "I found that DNS validation was the issue."),
            new(ChatRole.Assistant, "Great — DNS validation issues often manifest as SSL errors."),
        };

        string rendered = EpisodeExtractionPrompt.Render(
            job, turns, focus,
            knownDomains: ["Debugging", "Preferences"],
            recentFacts: []);

        string goldenPath = Path.Combine(
            AppContext.BaseDirectory,
            "Goldens", "MemoryExtraction_SslDebugging.txt");

        string goldenContent = File.ReadAllText(goldenPath)
            .Replace("\r\n", "\n")
            .Trim();

        string actual = rendered.Replace("\r\n", "\n").Trim();
        Assert.Equal(goldenContent, actual);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static MemoryRecord MakeRecord(string title, string value) =>
        MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: "Language",
            title: title,
            value: value,
            tags: [],
            importance: 0.7,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);
}
