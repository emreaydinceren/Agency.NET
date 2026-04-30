namespace Agency.GraphRAG.Code.Query;

/// <summary>
/// Configures model selection and context limits for the query pipeline.
/// </summary>
public sealed class QueryOptions
{
    /// <summary>Gets the model used for query classification.</summary>
    public string CheapestModel { get; init; } = "cheap";

    /// <summary>Gets the model used for final answer synthesis.</summary>
    public string AnswerModel { get; init; } = "standard";

    /// <summary>Gets the default context token budget.</summary>
    public int ContextTokenBudget { get; init; } = 600;
}
