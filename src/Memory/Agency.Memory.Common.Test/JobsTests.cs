using System.Text.Json;
using Agency.Memory.Common.Jobs;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for job payload types: <see cref="DistillationJob"/>, <see cref="ConsolidationJob"/>, and <see cref="DistillationTrigger"/>.</summary>
public sealed class JobsTests
{
    /// <summary>Verifies that <see cref="DistillationJob"/> has value semantics (equality by field values).</summary>
    [Fact]
    public void DistillationJob_Equality_ByValue()
    {
        var a = new DistillationJob("u1", "s1", DistillationTrigger.Inactivity, 5, "summary");
        var b = new DistillationJob("u1", "s1", DistillationTrigger.Inactivity, 5, "summary");
        var c = new DistillationJob("u1", "s1", DistillationTrigger.Inactivity, 99, null);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>Verifies that <see cref="DistillationJob"/> round-trips through System.Text.Json.</summary>
    [Fact]
    public void DistillationJob_RoundTrips_ThroughSystemTextJson()
    {
        var original = new DistillationJob("user-42", "sess-7", DistillationTrigger.GoalCompletion, 12, "goal achieved");

        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<DistillationJob>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original, deserialized);
    }

    /// <summary>Verifies that <see cref="DistillationTrigger"/> has exactly three values as specified.</summary>
    [Fact]
    public void DistillationTrigger_HasThreeValues_GoalCompletion_Inactivity_SessionDisposed()
    {
        var values = Enum.GetValues<DistillationTrigger>();

        Assert.Equal(3, values.Length);
        Assert.Contains(DistillationTrigger.GoalCompletion, values);
        Assert.Contains(DistillationTrigger.Inactivity, values);
        Assert.Contains(DistillationTrigger.SessionDisposed, values);
    }

    /// <summary>Verifies that <see cref="ConsolidationJob"/> has value semantics (equality by field values).</summary>
    [Fact]
    public void ConsolidationJob_Equality_ByValue()
    {
        var a = new ConsolidationJob("u1", "s1");
        var b = new ConsolidationJob("u1", "s1");
        var c = new ConsolidationJob("u1", "s2");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
