namespace Agency.Memory.Common.Options;

/// <summary>
/// Configuration for the consolidator background sub-agent.
/// </summary>
public sealed class ConsolidatorOptions
{
    /// <summary>Gets or sets when consolidation is triggered (default: <see cref="ConsolidationTrigger.OnSessionEnd"/>).</summary>
    public ConsolidationTrigger Trigger { get; set; } = ConsolidationTrigger.OnSessionEnd;

    /// <summary>Gets or sets the maximum number of sub-agent iterations per consolidation pass (default: 20).</summary>
    public int MaxIterations { get; set; } = 20;

    /// <summary>Gets or sets the cost ceiling in USD per consolidation pass (default: $0.50).</summary>
    public decimal MaxCostUsd { get; set; } = 0.50m;

    /// <summary>
    /// Gets or sets the model identifier used by the consolidator sub-agent.
    /// When <see langword="null"/>, the default model configured on the <see cref="Microsoft.Extensions.AI.IChatClient"/> is used.
    /// </summary>
    public string? Model { get; set; }
}
