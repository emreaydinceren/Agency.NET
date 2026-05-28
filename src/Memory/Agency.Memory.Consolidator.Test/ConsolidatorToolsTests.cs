using System.Text.Json;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.Tools;
using Moq;
using MemRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Consolidator.Test;

/// <summary>
/// Unit tests for the four consolidator tools: Merge, Update, Delete, Done.
/// </summary>
public class ConsolidatorToolsTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MemRecord MakeRecord(string id, string userId = "user1", string value = "old value", double importance = 0.5) =>
        MemRecord.Create(
            id: id,
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: id,
            title: "Test Record",
            value: value,
            tags: [],
            importance: importance,
            createdAt: _now.AddDays(-1),
            updatedAt: _now.AddDays(-1));

    // ── Memory_Merge ──────────────────────────────────────────────────────────

    /// <summary>
    /// MemoryMergeTool calls MergeAsync exactly once with the provided ids and new record.
    /// </summary>
    [Fact]
    public async Task MemoryMerge_CallsMergeAsync_Once()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        var newRecord = MakeRecord("new-1", value: "merged");
        store.Setup(s => s.MergeAsync(
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<MemRecord>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(newRecord);

        var tool = new MemoryMergeTool(store.Object, "user1");

        var input = JsonDocument.Parse("""
        {
            "recordIds": ["id-1", "id-2"],
            "newRecord": {
                "contentType": "Fact",
                "domain": "Test",
                "key": "merged-key",
                "title": "Merged",
                "value": "merged value",
                "tags": [],
                "importance": 0.8,
                "scope": "Global"
            }
        }
        """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        store.Verify(s => s.MergeAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.Count == 2 && ids[0] == "id-1" && ids[1] == "id-2"),
            It.Is<MemRecord>(r => r.Domain == "Test" && r.Key == "merged-key"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// MemoryMergeTool assigns a new GUID id to the new record (does not reuse existing ids).
    /// </summary>
    [Fact]
    public async Task MemoryMerge_NewRecord_GetsFreshId()
    {
        string capturedId = string.Empty;
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.MergeAsync(
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<MemRecord>(),
            It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, MemRecord, CancellationToken>((_, r, _) => capturedId = r.Id)
            .ReturnsAsync((IReadOnlyList<string> _, MemRecord r, CancellationToken _) => r);

        var tool = new MemoryMergeTool(store.Object, "user1");

        var input = JsonDocument.Parse("""
        {
            "recordIds": ["id-old"],
            "newRecord": {
                "contentType": "Fact",
                "domain": "Test",
                "key": "k1",
                "title": "T",
                "value": "v",
                "tags": [],
                "importance": 0.5,
                "scope": "Global"
            }
        }
        """).RootElement;

        await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(Guid.TryParse(capturedId, out _), $"Expected GUID id, got '{capturedId}'");
        Assert.DoesNotContain("id-old", capturedId);
    }

    // ── Memory_Update ─────────────────────────────────────────────────────────

    /// <summary>
    /// MemoryUpdateTool with only newValue updates value; calls UpdateAsync.
    /// </summary>
    [Fact]
    public async Task MemoryUpdate_OnlyValue_UpdatesValue()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.UpdateRecordAsync(
            "id-1",
            "user1",
            "new value",
            (double?)null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRecord("id-1", value: "new value"));

        var tool = new MemoryUpdateTool(store.Object, "user1");

        var input = JsonDocument.Parse("""
        {
            "recordId": "id-1",
            "newValue": "new value"
        }
        """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        store.Verify(s => s.UpdateRecordAsync("id-1", "user1", "new value", (double?)null, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// MemoryUpdateTool with only newImportance updates importance but not value.
    /// </summary>
    [Fact]
    public async Task MemoryUpdate_OnlyImportance_UpdatesImportance()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.UpdateRecordAsync(
            "id-1",
            "user1",
            (string?)null,
            0.9,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRecord("id-1", importance: 0.9));

        var tool = new MemoryUpdateTool(store.Object, "user1");

        var input = JsonDocument.Parse("""
        {
            "recordId": "id-1",
            "newImportance": 0.9
        }
        """).RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        store.Verify(s => s.UpdateRecordAsync("id-1", "user1", (string?)null, 0.9, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Memory_Delete ─────────────────────────────────────────────────────────

    /// <summary>
    /// MemoryDeleteTool hard-deletes the record by id and reports success.
    /// </summary>
    [Fact]
    public async Task MemoryDelete_HardDeletes_ReturnsSuccess()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.DeleteByIdAsync("id-1", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = new MemoryDeleteTool(store.Object, "user1");

        var input = JsonDocument.Parse("""{ "recordId": "id-1" }""").RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        store.Verify(s => s.DeleteByIdAsync("id-1", "user1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// MemoryDeleteTool returns an error when the record is not found.
    /// </summary>
    [Fact]
    public async Task MemoryDelete_NotFound_ReturnsError()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store.Setup(s => s.DeleteByIdAsync("id-missing", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var tool = new MemoryDeleteTool(store.Object, "user1");

        var input = JsonDocument.Parse("""{ "recordId": "id-missing" }""").RootElement;

        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── Memory_Done ───────────────────────────────────────────────────────────

    /// <summary>
    /// MemoryDoneTool signals stop by setting the done flag and returning success.
    /// </summary>
    [Fact]
    public async Task MemoryDone_SignalsStop()
    {
        bool doneCalled = false;
        var tool = new MemoryDoneTool(onDone: () => { doneCalled = true; });

        var input = JsonDocument.Parse("{}").RootElement;
        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError, result.Content);
        Assert.True(doneCalled);
    }
}
