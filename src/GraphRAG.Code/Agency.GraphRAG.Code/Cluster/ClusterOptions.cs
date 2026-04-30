namespace Agency.GraphRAG.Code.Cluster;

/// <summary>
/// Configures clustering behavior for the Phase 14 cluster layer.
/// </summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// Gets or sets how strongly project boundaries constrain clustering.
    /// </summary>
    public ProjectBoundaryMode ProjectBoundaryMode { get; set; } = ProjectBoundaryMode.Hard;

    /// <summary>
    /// Gets or sets the multiplier applied to intra-namespace reference edges.
    /// </summary>
    public double NamespaceWeightMultiplier { get; set; } = 1.5d;

    /// <summary>
    /// Gets or sets the multiplier applied to inter-project reference edges in soft mode.
    /// </summary>
    public double InterProjectWeightMultiplier { get; set; } = 0.5d;

    /// <summary>
    /// Gets or sets the resolution threshold used by the in-process partitioner.
    /// </summary>
    public double LeidenResolution { get; set; } = 0.2d;

    /// <summary>
    /// Gets or sets the random seed used to stabilize deterministic community identifiers.
    /// </summary>
    public int Seed { get; set; } = 17;

    /// <summary>
    /// Gets or sets the degree percentile used to identify utility-node candidates.
    /// </summary>
    public double UtilityDegreePercentile { get; set; } = 99d;

    /// <summary>
    /// Gets or sets the absolute minimum degree for utility-node candidates.
    /// </summary>
    public int UtilityDegreeFloor { get; set; } = 50;

    /// <summary>
    /// Gets or sets the normalized cluster-spread threshold used to identify utility-node candidates.
    /// </summary>
    public double UtilityClusterSpreadThreshold { get; set; } = 0.6d;

    /// <summary>
    /// Gets the naming hints that bias borderline nodes toward utility classification.
    /// </summary>
    public IReadOnlyList<string> UtilityNamingHints { get; init; } =
    [
        "*.Common",
        "*.Shared",
        "*.Infrastructure",
        "*.Utilities",
        "*.Abstractions",
    ];

    /// <summary>
    /// Gets or sets how utility nodes are assigned after the cleaned clustering pass.
    /// </summary>
    public UtilityAssignmentStrategy UtilityAssignmentStrategy { get; set; } = UtilityAssignmentStrategy.Dedicated;
}

/// <summary>
/// Controls how project boundaries influence cluster formation.
/// </summary>
public enum ProjectBoundaryMode
{
    /// <summary>No project-boundary adjustment is applied.</summary>
    Off = 0,

    /// <summary>No project-boundary adjustment is applied.</summary>
    None = Off,

    /// <summary>Inter-project edges are downweighted but still permitted.</summary>
    Soft = 1,

    /// <summary>Project boundaries are treated as inviolable top-level partitions.</summary>
    Hard = 2,
}

/// <summary>
/// Controls how utility nodes are assigned to clusters after the cleaned pass.
/// </summary>
public enum UtilityAssignmentStrategy
{
    /// <summary>Assigns utility nodes to a dedicated infrastructure cluster per project.</summary>
    Dedicated,

    /// <summary>Assigns utility nodes to the primary cluster most associated with their definition.</summary>
    ByDefinition,
}
