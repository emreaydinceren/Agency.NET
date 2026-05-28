namespace Agency.Memory.Hygiene;

/// <summary>
/// Captures the deletion counts from a single hygiene sweep pass.
/// </summary>
internal sealed record SweepResult(int TtlDeleted, int ImportanceDeleted)
{
    /// <summary>Gets the total number of records deleted across all passes.</summary>
    public int TotalDeleted => this.TtlDeleted + this.ImportanceDeleted;
}
