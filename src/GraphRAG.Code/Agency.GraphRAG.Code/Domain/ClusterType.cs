namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Classifies the primary domain concern of a <see cref="Cluster"/> of symbols.
/// </summary>
public enum ClusterType
{
    /// <summary>The cluster primarily contains business-domain logic.</summary>
    Business,

    /// <summary>The cluster primarily contains infrastructure or cross-cutting concerns.</summary>
    Infrastructure,

    /// <summary>The cluster contains a mix of business and infrastructure concerns.</summary>
    Mixed,
}
