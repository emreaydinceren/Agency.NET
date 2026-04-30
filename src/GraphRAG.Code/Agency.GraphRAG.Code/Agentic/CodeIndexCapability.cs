using Agency.GraphRAG.Code.Query;

namespace Agency.GraphRAG.Code.Agentic;

/// <summary>
/// Default <see cref="ICodeIndex"/> implementation backed by <see cref="QueryPipeline"/>.
/// </summary>
public sealed class CodeIndexCapability(QueryPipeline queryPipeline) : ICodeIndex
{
    /// <inheritdoc />
    public async Task<string> AskAsync(string question, int topK = 5, CancellationToken cancellationToken = default)
    {
        _ = topK;
        QueryResponse response = await queryPipeline.ExecuteAsync(question, cancellationToken).ConfigureAwait(false);
        return response.Answer;
    }
}
