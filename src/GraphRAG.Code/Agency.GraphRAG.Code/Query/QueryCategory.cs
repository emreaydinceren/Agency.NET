namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Classifies the broad retrieval strategy for a user query.
/// </summary>
public enum QueryCategory
{
    Local,
    Subsystem,
    Global,
    Impact,
    Dependency,
}
