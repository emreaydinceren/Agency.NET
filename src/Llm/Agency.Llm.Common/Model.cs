namespace Agency.Llm.Common;

/// <summary>Identifies an LLM model available from a provider.</summary>
/// <remarks>
/// The optional members below are enrichment: a provider populates them only where its
/// server actually answers the question, and leaves them <see langword="null"/> everywhere
/// else. <see langword="null"/> always means "unknown", never a claim that the value is
/// false or absent (see spec principle P3). No feature may depend on a vendor-specific
/// endpoint to function (P2); these members are always optional.
/// </remarks>
public sealed record Model(string Id, string Name)
{
    /// <summary>
    /// The kind of model (chat, embedding, vision), or <see langword="null"/> if the
    /// provider did not report it. <see langword="null"/> means unknown, not
    /// <see cref="ModelKind.Unknown"/> — that value is reserved for a provider that reports a
    /// kind it does not recognize.
    /// </summary>
    public ModelKind? Kind { get; init; }

    /// <summary>
    /// The model's context window size in tokens, or <see langword="null"/> if the provider
    /// did not report it. Never <c>0</c> to mean unknown.
    /// </summary>
    public int? ContextLength { get; init; }

    /// <summary>
    /// Whether the model is currently loaded and ready to serve requests, or
    /// <see langword="null"/> if the provider did not report load state. Never
    /// <see langword="false"/> to mean unknown.
    /// </summary>
    public bool? IsLoaded { get; init; }
}
