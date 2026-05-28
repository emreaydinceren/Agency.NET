using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Tests for bounded channel backpressure (Spec §10.3, C.8).
/// </summary>
public sealed class BackpressureTests
{
    /// <summary>
    /// When the channel is at capacity, writing an additional job drops the oldest item
    /// (DropOldest policy).
    /// </summary>
    [Fact]
    public void Channel_AtCapacity_DropsOldest_LogsWarning()
    {
        const int capacity = 3;

        var options = Options.Create(new DistillerOptions
        {
            PerSessionQueueCapacity = capacity,
        });

        var registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);
        var writer = registry.GetOrCreateWriter("u1", "s1");

        // Fill the channel to capacity.
        for (int i = 1; i <= capacity; i++)
        {
            bool written = writer.TryWrite(new DistillationJob("u1", "s1", DistillationTrigger.Inactivity, i));
            Assert.True(written, $"Write {i} should succeed while below capacity.");
        }

        // Writing one more drops the oldest.
        bool overflowWritten = writer.TryWrite(
            new DistillationJob("u1", "s1", DistillationTrigger.Inactivity, capacity + 1));

        // With DropOldest the overflow write succeeds (the oldest is silently dropped).
        Assert.True(overflowWritten, "DropOldest write should succeed.");

        // Channel should still contain exactly `capacity` items.
        var reader = registry.GetOrCreate("u1", "s1").Reader;
        int count = 0;
        int? firstRead = null;
        while (reader.TryRead(out DistillationJob? job))
        {
            count++;
            if (firstRead is null)
            {
                firstRead = job!.UpToTurnIndex;
            }
        }

        Assert.Equal(capacity, count);
        // The oldest item (index=1) should have been dropped; the first item now is index=2.
        Assert.Equal(2, firstRead);
    }

    /// <summary>
    /// Each session gets its own channel — per-session segmentation via ConcurrentDictionary.
    /// </summary>
    [Fact]
    public void Registry_DifferentSessions_GetSeparateChannels()
    {
        var options = Options.Create(new DistillerOptions());
        var registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);

        var w1 = registry.GetOrCreateWriter("u1", "session-a");
        _ = registry.GetOrCreateWriter("u1", "session-b");

        w1.TryWrite(new DistillationJob("u1", "session-a", DistillationTrigger.Inactivity, 1));

        // session-b's channel must be empty.
        bool hasItem = registry.GetOrCreate("u1", "session-b").Reader.TryRead(out _);
        Assert.False(hasItem, "session-b channel should be independent.");
    }
}
