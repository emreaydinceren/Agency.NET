namespace Agency.Memory.Common.Records;

/// <summary>
/// Distinguishes the origin/source of a memory <see cref="Record"/>.
/// Used by the Consolidator and Hygiene sweeper to make provenance-aware decisions.
/// </summary>
public enum MemorySource
{
    /// <summary>Explicitly saved by the agent via the MemorizeNow tool (immediate persistence).</summary>
    AgentSignaled = 0,

    /// <summary>Automatically extracted by the Distiller from conversation (post-session).</summary>
    Distilled = 1,

    /// <summary>Created or merged by the Consolidator during reconciliation.</summary>
    Consolidated = 2,

    /// <summary>Created by Hygiene as a cleanup artifact (e.g., TTL replacement).</summary>
    HygieneManual = 3,
}
