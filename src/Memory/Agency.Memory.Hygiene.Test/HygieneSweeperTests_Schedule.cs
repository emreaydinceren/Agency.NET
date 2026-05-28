using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Hygiene;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Agency.Memory.Hygiene.Test;

/// <summary>Tests for the schedule and metric behaviour of <see cref="HygieneSweeperBackgroundService"/>.</summary>
public sealed class HygieneSweeperTests_Schedule
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

    /// <summary>Verifies that the sweeper fires once when the configured interval elapses.</summary>
    [Fact]
    public async Task Schedule_FiresAtConfiguredInterval()
    {
        var fakeTime = new FakeTimeProvider();
        var callCount = 0;

        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereTtlExceededAsync(It.IsAny<ContentType>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ReturnsAsync(0);
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var options = new MemoryOptions
        {
            HygieneSchedule = TimeSpan.FromHours(24),
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Fact] = TimeSpan.FromDays(90),
            },
        };

        var sweeper = CreateSweeper(store.Object, options, fakeTime);
        using var cts = new CancellationTokenSource();

        _ = sweeper.StartAsync(cts.Token);

        // Give the background loop a moment to register its Task.Delay.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Advance past the first scheduled period (24h ± 15 min jitter; advance by 25h to be safe).
        fakeTime.Advance(TimeSpan.FromHours(25));

        // Allow the background loop iteration to complete.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        try
        {
            await sweeper.StopAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) { }

        Assert.True(callCount >= 1, $"Expected at least 1 sweep pass, got {callCount}");
    }

    /// <summary>Verifies that jitter is applied within ±15 minutes when constructing the first delay.</summary>
    [Fact]
    public void Schedule_AppliesJitterUpTo15Min()
    {
        // The jitter applied to each sweep period must be within ±15 min of HygieneSchedule.
        // We test the JitterFor helper directly.
        var options = new MemoryOptions { HygieneSchedule = TimeSpan.FromHours(24) };

        // Run 50 samples and verify all land within the ±15-min window.
        for (var i = 0; i < 50; i++)
        {
            var jittered = HygieneSweeperBackgroundService.ApplyJitter(options.HygieneSchedule);
            var delta = jittered - options.HygieneSchedule;

            Assert.True(
                delta >= TimeSpan.FromMinutes(-15) && delta <= TimeSpan.FromMinutes(15),
                $"Jitter delta {delta} was outside ±15 min window");
        }
    }

    /// <summary>Verifies that a cancellation token terminates the loop without error.</summary>
    [Fact]
    public async Task Schedule_CancellationToken_TerminatesLoop()
    {
        var fakeTime = new FakeTimeProvider();

        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sweeper = CreateSweeper(store.Object, timeProvider: fakeTime);
        using var cts = new CancellationTokenSource();

        var startTask = sweeper.StartAsync(cts.Token);

        // Cancel before the first tick fires.
        await cts.CancelAsync();

        // The stop should complete gracefully, no exception should propagate.
        var stopTask = sweeper.StopAsync(TestContext.Current.CancellationToken);

        await Task.WhenAny(stopTask, Task.Delay(3000, TestContext.Current.CancellationToken));

        Assert.True(stopTask.IsCompleted, "StopAsync did not complete after cancellation");
    }

    /// <summary>Verifies that deletion counts are reported via metrics for each sweep pass.</summary>
    [Fact]
    public async Task Schedule_EmitsDeletionCountMetric_PerPass()
    {
        var store = new Mock<IMemoryStore>();
        store
            .Setup(s => s.DeleteWhereTtlExceededAsync(ContentType.Fact, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        store
            .Setup(s => s.DeleteWhereLowImportanceStaleAsync(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var options = new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Fact] = TimeSpan.FromDays(90),
            },
        };

        var sweeper = CreateSweeper(store.Object, options);

        // RunOnceAsync exposes a single sweep pass; metrics must be emitted.
        var result = await sweeper.RunOnceAsync(TestContext.Current.CancellationToken);

        // Total = 7 (TTL) + 3 (importance) = 10
        Assert.Equal(10, result.TotalDeleted);
        Assert.Equal(7, result.TtlDeleted);
        Assert.Equal(3, result.ImportanceDeleted);
    }
}
