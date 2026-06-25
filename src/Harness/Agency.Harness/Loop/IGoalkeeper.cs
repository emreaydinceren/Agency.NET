namespace Agency.Harness.Loop;

/// <summary>
/// Deterministic done-check gate: given the goal condition and the conversation so far,
/// returns a <see cref="Verdict"/> that tells the <c>LoopRunner</c> whether to continue
/// or stop. The Goalkeeper is transcript-only — it never calls tools (NG-4).
/// </summary>
internal interface IGoalkeeper
{
    /// <summary>
    /// Evaluates the current conversation against the goal condition.
    /// </summary>
    /// <param name="condition">
    /// The verifiable end-state from <see cref="GoalSpec.Condition"/>.
    /// </param>
    /// <param name="transcript">
    /// The full conversation history produced by the worker so far.
    /// </param>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <returns>
    /// <see cref="Verdict.Done"/> when the condition is satisfied;
    /// <see cref="Verdict.Continue"/> otherwise (with the reason used as the next directive).
    /// </returns>
    Task<Verdict> EvaluateAsync(
        string condition,
        IReadOnlyList<ChatMessage> transcript,
        CancellationToken ct);
}
