using Agency.Agentic;
using Agency.Memory.Common.Events;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// Verifies that both the success and failure distillation events are
    /// <see cref="DistillationSettledEvent"/>s, so a single terminal observable covers
    /// either outcome (TI-8.1).
    /// </summary>
    [Fact]
    public void DistillationCompletedAndFailed_AreBothSettledEvents()
    {
        DistillationSettledEvent completed = new DistillationCompletedEvent("u1", "s1", 3, 7);
        DistillationSettledEvent failed = new DistillationFailedEvent("u1", "s1", "LLM 429", true);

        Assert.Equal("u1", completed.UserId);
        Assert.Equal("s1", failed.SessionId);
    }

    /// <summary>
    /// Verifies that a subscriber to the <see cref="DistillationSettledEvent"/> base type receives
    /// both a published <see cref="DistillationCompletedEvent"/> and a published
    /// <see cref="DistillationFailedEvent"/> — the polymorphic dispatch that lets the failure path
    /// act as a terminal observable (TI-8.1).
    /// </summary>
    [Fact]
    public async Task EventBus_BaseTypeSubscriber_ReceivesBothTerminalOutcomes()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var settled = new List<DistillationSettledEvent>();
        using IDisposable sub = bus.Subscribe<DistillationSettledEvent>((evt, _) =>
        {
            settled.Add(evt);
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new DistillationCompletedEvent("u1", "s1", 1, 1), ct);
        await bus.PublishAsync(new DistillationFailedEvent("u1", "s2", "boom", true), ct);

        Assert.Equal(2, settled.Count);
        Assert.Contains(settled, e => e is DistillationCompletedEvent);
        Assert.Contains(settled, e => e is DistillationFailedEvent);
    }

    /// <summary>
    /// Verifies that polymorphic dispatch does not over-deliver: a subscriber to the concrete
    /// <see cref="DistillationCompletedEvent"/> is not invoked for a published
    /// <see cref="DistillationFailedEvent"/>.
    /// </summary>
    [Fact]
    public async Task EventBus_ConcreteSubscriber_IsNotInvokedForSiblingType()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var completed = new List<DistillationCompletedEvent>();
        using IDisposable sub = bus.Subscribe<DistillationCompletedEvent>((evt, _) =>
        {
            completed.Add(evt);
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new DistillationFailedEvent("u1", "s1", "boom", true), ct);

        Assert.Empty(completed);
    }
}
