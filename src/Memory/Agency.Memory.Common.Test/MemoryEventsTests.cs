using Agency.Agentic;
using Agency.Memory.Common.Events;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for memory-specific <see cref="AgentEvent"/> payloads.</summary>
public sealed class MemoryEventsTests
{
    /// <summary>Verifies that <see cref="DistillationCompletedEvent"/> has the required fields and inherits from <see cref="AgentEvent"/>.</summary>
    [Fact]
    public void DistillationCompletedEvent_HasRequiredFields_AndIsAgentEvent()
    {
        var evt = new DistillationCompletedEvent("u1", "s1", RecordsWritten: 3, NewWatermark: 7);

        Assert.IsAssignableFrom<AgentEvent>(evt);
        Assert.Equal("u1", evt.UserId);
        Assert.Equal("s1", evt.SessionId);
        Assert.Equal(3, evt.RecordsWritten);
        Assert.Equal(7, evt.NewWatermark);
    }

    /// <summary>Verifies that <see cref="DistillationFailedEvent"/> has the required fields and inherits from <see cref="AgentEvent"/>.</summary>
    [Fact]
    public void DistillationFailedEvent_HasRequiredFields_AndIsAgentEvent()
    {
        var evt = new DistillationFailedEvent("u1", "s1", Reason: "LLM 429", DeadLettered: true);

        Assert.IsAssignableFrom<AgentEvent>(evt);
        Assert.Equal("u1", evt.UserId);
        Assert.Equal("s1", evt.SessionId);
        Assert.Equal("LLM 429", evt.Reason);
        Assert.True(evt.DeadLettered);
    }

    /// <summary>Verifies that <see cref="ConsolidationCompletedEvent"/> has the required fields and inherits from <see cref="AgentEvent"/>.</summary>
    [Fact]
    public void ConsolidationCompletedEvent_HasRequiredFields_AndIsAgentEvent()
    {
        var evt = new ConsolidationCompletedEvent("u1", Merges: 2, Updates: 1, Deletes: 3);

        Assert.IsAssignableFrom<AgentEvent>(evt);
        Assert.Equal("u1", evt.UserId);
        Assert.Equal(2, evt.Merges);
        Assert.Equal(1, evt.Updates);
        Assert.Equal(3, evt.Deletes);
    }

    /// <summary>Verifies that event types are distinguishable by type discriminator (pattern matching).</summary>
    [Fact]
    public void MemoryEvents_AreDistinguishableViaPatternMatching()
    {
        AgentEvent[] events =
        [
            new DistillationCompletedEvent("u", "s", 1, 1),
            new DistillationFailedEvent("u", "s", "err", false),
            new ConsolidationCompletedEvent("u", 0, 0, 0),
        ];

        int completedCount = 0;
        int failedCount = 0;
        int consolidatedCount = 0;

        foreach (var evt in events)
        {
            switch (evt)
            {
                case DistillationCompletedEvent: completedCount++; break;
                case DistillationFailedEvent: failedCount++; break;
                case ConsolidationCompletedEvent: consolidatedCount++; break;
            }
        }

        Assert.Equal(1, completedCount);
        Assert.Equal(1, failedCount);
        Assert.Equal(1, consolidatedCount);
    }

    /// <summary>Verifies value semantics of memory event records.</summary>
    [Fact]
    public void DistillationCompletedEvent_HasValueSemantics()
    {
        var a = new DistillationCompletedEvent("u1", "s1", 3, 7);
        var b = new DistillationCompletedEvent("u1", "s1", 3, 7);
        var c = new DistillationCompletedEvent("u1", "s1", 5, 7);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
