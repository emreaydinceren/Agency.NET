namespace Agency.GraphRAG.Code.Agentic;

/// <summary>
/// Exposes code-index question answering to higher-level agent integrations.
/// </summary>
public interface ICodeIndex
{
    /// <summary>
    /// Answers a code question using the indexed graph.
    /// </summary>
    /// <param name="question">The question to answer.</param>
    /// <param name="topK">The maximum number of matches to include.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The textual answer.</returns>
    Task<string> AskAsync(string question, int topK = 5, CancellationToken cancellationToken = default);
}
