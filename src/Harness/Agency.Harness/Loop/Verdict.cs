using System.Text.Json.Serialization;

namespace Agency.Harness.Loop;

/// <summary>The Goalkeeper's decision after evaluating a turn against the goal condition.</summary>
[JsonDerivedType(typeof(Continue), typeDiscriminator: "continue")]
[JsonDerivedType(typeof(Done), typeDiscriminator: "done")]
public abstract record Verdict
{
    /// <summary>
    /// The goal condition is not yet satisfied.
    /// <paramref name="Reason"/> is fed back as the next turn's directive so the worker self-corrects.
    /// </summary>
    public sealed record Continue(string Reason) : Verdict;

    /// <summary>
    /// The goal condition is satisfied. The loop exits with <see cref="LoopOutcome.Achieved"/>.
    /// <paramref name="Reason"/> is recorded in the <see cref="LoopResultEvent"/> for observability.
    /// </summary>
    public sealed record Done(string Reason) : Verdict;
}
