using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Pipeline;

/// <summary>Builds <see cref="Phase1WriteRequest"/> instances from a repository walk result.</summary>
internal interface IWriteRequestBuilder
{
    /// <summary>
    /// Builds write requests for all processable files in the walk result.
    /// </summary>
    /// <param name="repo">The repository being indexed.</param>
    /// <param name="walkResult">The result of the repository walk.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary mapping file paths to their write requests.</returns>
    Task<IReadOnlyDictionary<string, Phase1WriteRequest>> BuildAsync(
        Repo repo,
        WalkResult walkResult,
        CancellationToken cancellationToken = default,
        Action<string>? onProgress = null);
}
