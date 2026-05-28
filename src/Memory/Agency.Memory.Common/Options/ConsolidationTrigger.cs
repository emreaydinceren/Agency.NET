namespace Agency.Memory.Common.Options;

/// <summary>
/// Determines when the Consolidator is triggered for a user.
/// </summary>
public enum ConsolidationTrigger
{
    /// <summary>Run consolidation automatically when a session ends (V1 default).</summary>
    OnSessionEnd,

    /// <summary>Run consolidation only when explicitly triggered via API.</summary>
    Manual,
}
