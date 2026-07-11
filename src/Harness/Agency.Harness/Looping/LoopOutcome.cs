namespace Agency.Harness.Looping;

/// <summary>Why a loop run ended.</summary>
public enum LoopOutcome
{
    /// <summary>The Goalkeeper confirmed the goal condition was satisfied.</summary>
    Achieved,

    /// <summary>The hard <see cref="GoalSpec.MaxTurns"/> ceiling was reached without achieving the goal.</summary>
    CapReached,

    /// <summary>The hard <see cref="GoalSpec.Budget"/> or <see cref="GoalSpec.TokenBudget"/> ceiling was reached.</summary>
    BudgetExceeded,

    /// <summary>An unrecoverable error occurred in an inner turn.</summary>
    Error,

    /// <summary>The loop was cancelled via <see cref="System.Threading.CancellationToken"/>.</summary>
    Cancelled,
}
