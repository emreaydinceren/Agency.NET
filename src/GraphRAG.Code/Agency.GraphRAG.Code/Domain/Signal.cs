namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a signal or evidence source that contributed to inferring an <see cref="Edge"/>.
/// </summary>
public enum Signal
{
    /// <summary>The edge was inferred from a direct name match between identifiers.</summary>
    NameMatch,

    /// <summary>The edge was extracted by an LLM reasoning step.</summary>
    LlmExtraction,

    /// <summary>The target is likely an external dependency rather than a local symbol.</summary>
    ExternalLikely,

    /// <summary>The edge could not be resolved to a known target.</summary>
    Unresolved,
}
