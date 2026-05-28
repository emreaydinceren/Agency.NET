using System.Threading.Channels;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Tests for <see cref="InactivityTimerService"/> per-session timer behaviour (Spec §10, §6.2).
/// </summary>
public sealed class InactivityTimerServiceTests
{
    private static (InactivityTimerService Timer, ChannelSessionRegistry Registry, FakeTimeProvider Clock)
        CreateTimer(TimeSpan? timeout = null)
    {
        var options = Options.Create(new DistillerOptions
        {
            InactivityTimeout = timeout ?? TimeSpan.FromSeconds(5),
        });
        var loggerRegistry = NullLogger<ChannelSessionRegistry>.Instance;
        var registry = new ChannelSessionRegistry(options, loggerRegistry);
        var clock = new FakeTimeProvider();
        var timer = new InactivityTimerService(
            registry, options, clock,
            NullLogger<InactivityTimerService>.Instance);
        return (timer, registry, clock);
    }

    /// <summary>Calling Restart for the first time starts a timer for the session.</summary>
    [Fact]
    public async Task Start_FirstCall_StartsTimer()
    {
        var (timer, registry, clock) = CreateTimer(timeout: TimeSpan.FromSeconds(5));

        timer.Restart("u1", "session1", currentTurnIndex: 2);

        // Advance clock past timeout.
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "session1");
        bool hasItem = ch.Reader.TryRead(out DistillationJob? job);
        Assert.True(hasItem);
        Assert.Equal(DistillationTrigger.Inactivity, job!.Trigger);
        Assert.Equal("u1", job.UserId);
        Assert.Equal("session1", job.SessionId);
        Assert.Equal(2, job.UpToTurnIndex);
    }

    /// <summary>Restarting the timer resets it — it should not fire at the original schedule.</summary>
    [Fact]
    public async Task Restart_ResetsTimer_DoesNotFireEarly()
    {
        var (timer, registry, clock) = CreateTimer(timeout: TimeSpan.FromSeconds(5));

        timer.Restart("u1", "session1", currentTurnIndex: 1);

        // Advance 3s — halfway through timeout.
        clock.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Restart resets the clock.
        timer.Restart("u1", "session1", currentTurnIndex: 2);

        // Advance another 4s (timer reset at t=3, now 4s into new 5s timer).
        clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Should NOT have fired yet.
        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "session1");
        bool hasItem = ch.Reader.TryRead(out _);
        Assert.False(hasItem, "Timer should not have fired yet after restart.");

        // Now advance past the timeout.
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        hasItem = ch.Reader.TryRead(out DistillationJob? job);
        Assert.True(hasItem);
        Assert.Equal(2, job!.UpToTurnIndex); // latest turn index from last Restart
    }

    /// <summary>When the timer expires it enqueues a DistillationJob with Inactivity trigger.</summary>
    [Fact]
    public async Task Expiry_EnqueuesDistillationJob_WithInactivityTrigger()
    {
        var (timer, registry, clock) = CreateTimer(timeout: TimeSpan.FromSeconds(5));
        timer.Restart("u1", "session1", currentTurnIndex: 10);

        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "session1");
        Assert.True(ch.Reader.TryRead(out DistillationJob? job));
        Assert.Equal(DistillationTrigger.Inactivity, job!.Trigger);
        Assert.Equal(10, job.UpToTurnIndex);
    }

    /// <summary>Calling Stop cancels the timer — it does not fire after.</summary>
    [Fact]
    public async Task Stop_OnDispose_CancelsTimer_NoFireAfter()
    {
        var (timer, registry, clock) = CreateTimer(timeout: TimeSpan.FromSeconds(5));
        timer.Restart("u1", "session1", currentTurnIndex: 5);

        timer.Stop("session1");

        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "session1");
        bool hasItem = ch.Reader.TryRead(out _);
        Assert.False(hasItem, "Timer should not fire after Stop.");
    }

    /// <summary>Calling Restart after expiry starts a fresh timer.</summary>
    [Fact]
    public async Task Restart_AfterExpiry_StartsFreshTimer()
    {
        var (timer, registry, clock) = CreateTimer(timeout: TimeSpan.FromSeconds(5));

        // First expiry.
        timer.Restart("u1", "session1", currentTurnIndex: 1);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "session1");
        ch.Reader.TryRead(out _); // consume first job

        // Start fresh timer.
        timer.Restart("u1", "session1", currentTurnIndex: 5);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        bool hasItem = ch.Reader.TryRead(out DistillationJob? job);
        Assert.True(hasItem);
        Assert.Equal(5, job!.UpToTurnIndex);
    }
}
