using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Hygiene;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Agency.Memory.Hygiene.Test;

/// <summary>Tests for the TTL-based pruning pass of <see cref="HygieneSweeperBackgroundService"/>.</summary>
public sealed class HygieneSweeperTests_Ttl
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

    /// <summary>Verifies that facts older than the configured TTL and not recently accessed are deleted.</summary>
    [Fact]
    public async Task Ttl_FactsOlderThanFactTtl_AndNotAccessedSince_Deleted()
    {
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereTtlExceededAsync(ContentType.Fact, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        store
            .Setup(s => s.DeleteWhereTtlExceededAsync(ContentType.Memory, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var options = new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Fact] = TimeSpan.FromDays(90),
            },
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereTtlExceededAsync(ContentType.Fact, TimeSpan.FromDays(90), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Verifies that facts accessed recently survive the TTL pass (LastAccessedAt reset behaviour is handled by the store).</summary>
    [Fact]
    public async Task Ttl_FactsAccessedRecently_NotDeleted()
    {
        // The store is responsible for the LastAccessedAt check; the sweeper
        // just calls DeleteWhereTtlExceededAsync and trusts the store to honour it.
        // We assert that the sweeper always delegates even when 0 rows are deleted.
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereTtlExceededAsync(ContentType.Fact, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // store returns 0 because recently-accessed records survived
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var options = new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Fact] = TimeSpan.FromDays(90),
            },
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        // Call was made; store correctly returned 0.
        store.Verify(
            s => s.DeleteWhereTtlExceededAsync(ContentType.Fact, TimeSpan.FromDays(90), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Verifies that content types with no configured TTL are skipped entirely.</summary>
    [Fact]
    public async Task Ttl_NoTtlConfiguredForContentType_NoDeletes()
    {
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // No TTL configured for any content type.
        var options = new MemoryOptions { Ttl = [] };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereTtlExceededAsync(It.IsAny<ContentType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Verifies that distinct TTLs per content type are applied independently.</summary>
    [Fact]
    public async Task Ttl_DistinctTtlsPerContentType_AppliedIndependently()
    {
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereTtlExceededAsync(It.IsAny<ContentType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var options = new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Fact] = TimeSpan.FromDays(90),
                [ContentType.Memory] = TimeSpan.FromDays(365),
            },
        };

        var sweeper = CreateSweeper(store.Object, options);

        await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Verify(
            s => s.DeleteWhereTtlExceededAsync(ContentType.Fact, TimeSpan.FromDays(90), It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(
            s => s.DeleteWhereTtlExceededAsync(ContentType.Memory, TimeSpan.FromDays(365), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
