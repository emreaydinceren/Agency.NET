namespace Agency.Memory.Common.Jobs;

/// <summary>Identifies what caused a <see cref="DistillationJob"/> to be enqueued.</summary>
public enum DistillationTrigger
{
    /// <summary>The agent explicitly called the <c>MarkGoalComplete</c> tool.</summary>
    GoalCompletion,

    /// <summary>The inactivity timer expired before a new user turn arrived.</summary>
    Inactivity,

    /// <summary>The chat session was disposed (end-of-session cleanup).</summary>
    SessionDisposed,
}
