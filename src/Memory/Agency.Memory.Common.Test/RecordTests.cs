using Agency.Memory.Common.Records;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for the <see cref="MemoryRecord"/> type and <see cref="ContentType"/> enum.</summary>
public sealed class RecordTests
{
    /// <summary>Verifies that a <see cref="MemoryRecord"/> can be constructed with all required fields.</summary>
    [Fact]
    public void Record_RequiredFieldsAreEnforcedAtConstruction()
    {
        var now = DateTimeOffset.UtcNow;

        var record = MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: "Language",
            title: "Python preference",
            value: "User prefers Python.",
            tags: ["python", "language"],
            importance: 0.7,
            createdAt: now,
            updatedAt: now);

        Assert.Equal("r1", record.Id);
        Assert.Equal("u1", record.UserId);
        Assert.Null(record.SessionId);
        Assert.Equal(ContentType.Fact, record.ContentType);
        Assert.Equal("Preferences", record.Domain);
        Assert.Equal("Language", record.Key);
        Assert.Equal("Python preference", record.Title);
        Assert.Equal("User prefers Python.", record.Value);
        Assert.Equal(["python", "language"], record.Tags);
        Assert.Equal(0.7, record.Importance);
    }

    /// <summary>Verifies that <see cref="MemoryRecord.Age"/> is derived from <see cref="MemoryRecord.UpdatedAt"/>.</summary>
    [Fact]
    public void Record_AgeIsDerivedFromUpdatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var threeDaysAgo = now - TimeSpan.FromDays(3);

        var record = MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "D",
            key: "K",
            title: "T",
            value: "V",
            tags: [],
            importance: 0.5,
            createdAt: threeDaysAgo,
            updatedAt: threeDaysAgo);

        var expectedAge = TimeSpan.FromDays(3);
        var diff = record.Age - expectedAge;
        Assert.True(Math.Abs(diff.TotalSeconds) < 1, $"Age was {record.Age} but expected ~3 days");
    }

    /// <summary>Verifies that <see cref="MemoryRecord"/> factory throws when Importance is below zero.</summary>
    [Fact]
    public void Record_ImportanceBelowZero_IsInvalid()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "D",
            key: "K",
            title: "T",
            value: "V",
            tags: [],
            importance: -0.1,
            createdAt: now,
            updatedAt: now));
    }

    /// <summary>Verifies that <see cref="MemoryRecord"/> factory throws when Importance exceeds 1.0.</summary>
    [Fact]
    public void Record_ImportanceAboveOne_IsInvalid()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "D",
            key: "K",
            title: "T",
            value: "V",
            tags: [],
            importance: 1.5,
            createdAt: now,
            updatedAt: now));
    }

    /// <summary>Verifies the underlying integer values of <see cref="ContentType"/> match the DB SMALLINT column spec.</summary>
    [Fact]
    public void ContentType_EnumValuesAre_FactZero_MemoryOne()
    {
        Assert.Equal(0, (short)ContentType.Fact);
        Assert.Equal(1, (short)ContentType.Memory);
    }
}
