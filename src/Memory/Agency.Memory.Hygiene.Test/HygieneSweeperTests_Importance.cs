using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Hygiene;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Agency.Memory.Hygiene.Test;

/// <summary>Tests for the importance-based pruning pass of <see cref="HygieneSweeperBackgroundService"/>.</summary>
public sealed class HygieneSweeperTests_Importance
{
    /// <summary>Creates a default <see cref="HygieneSweeperBackgroundService"/> for testing.</summary>
    private static HygieneSweeperBackgroundService CreateSweeper(
        IMemoryStore store,
        MemoryOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var opts = Options.Create(options ?? new MemoryOptions());
        return new HygieneSweeperBackgroundService(
            store,
            opts,
            timeProvider ?? TimeProvider.System,
            NullLogger<HygieneSweeperBackgroundService>.Instance);
    }

    /// <summary>Verifies that low-importance stale records are deleted.</summary>
    [Fact]
    public async Task Importance_LowImportance_StaleAge_Deleted()
    {
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(0.2, TimeSpan.FromDays(30), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var options = new MemoryOptions
        {
            ImportancePruneThreshold = 0.2,
            StalePruneAge = TimeSpan.FromDays(30),
            Ttl = [],
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereLowImportanceStaleAsync(0.2, TimeSpan.FromDays(30), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Verifies that low-importance but recently-accessed records are not deleted (store handles this).</summary>
    [Fact]
    public async Task Importance_LowImportance_RecentlyAccessed_NotDeleted()
    {
        var store = new Mock<IMemoryStore>();
        // Store returns 0 because recently-accessed records survived the predicate inside the store.
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sweeper = CreateSweeper(store.Object);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        // The sweeper must still call the method; the store correctly returned 0.
        store.Verify(
            s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Verifies that high-importance stale records are not deleted.</summary>
    [Fact]
    public async Task Importance_HighImportance_StaleAge_NotDeleted()
    {
        var store = new Mock<IMemoryStore>();
        // Store returns 0 because high-importance records survived the importance < threshold predicate.
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var options = new MemoryOptions
        {
            ImportancePruneThreshold = 0.2,
            StalePruneAge = TimeSpan.FromDays(30),
            Ttl = [],
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereLowImportanceStaleAsync(0.2, TimeSpan.FromDays(30), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
