namespace Agency.Memory.Common.Options;

/// <summary>
/// Determines how the bounded distillation channel behaves when it reaches capacity.
/// </summary>
public enum BackpressurePolicy
{
    /// <summary>Drops the oldest pending job to make room for the new one.</summary>
    DropOldest,

    /// <summary>Drops the incoming new job rather than displacing an existing one.</summary>
    DropNewest,

    /// <summary>Waits until capacity is available before accepting the new job.</summary>
    Wait,
}
