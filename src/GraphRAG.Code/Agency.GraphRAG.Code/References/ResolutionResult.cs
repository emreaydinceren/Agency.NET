using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.References;

/// <summary>
/// Represents a scored reference-resolution outcome for one candidate target or unresolved target.
/// </summary>
/// <param name="TargetSymbolId">The resolved target symbol identifier when available.</param>
/// <param name="Confidence">The computed confidence in the range [0, 1].</param>
/// <param name="Signals">The evidence signals that contributed to the result.</param>
/// <param name="ExternalPackageName">The matched external package name when the target is external.</param>
public sealed record ResolutionResult(
    Guid? TargetSymbolId,
    double Confidence,
    IReadOnlyList<Signal> Signals,
    string? ExternalPackageName = null);
