namespace Agency.Memory.Common.Records;

/// <summary>
/// Agent-facing importance level for a memory record, mapped to a <see cref="Record.Importance"/>
/// double at write time (High = 0.9, Normal = 0.6, Low = 0.3).
/// </summary>
public enum Importance
{
    /// <summary>Reshapes future decisions; maps to 0.9.</summary>
    High = 0,

    /// <summary>Useful reference, standard scenario; maps to 0.6.</summary>
    Normal = 1,

    /// <summary>Edge case, rare, context-specific; maps to 0.3.</summary>
    Low = 2,
}
