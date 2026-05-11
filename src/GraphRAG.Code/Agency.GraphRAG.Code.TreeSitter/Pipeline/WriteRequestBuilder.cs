using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;
using Microsoft.Extensions.Logging;

namespace Agency.GraphRAG.Code.TreeSitter.Pipeline;

/// <summary>
/// Builds <see cref="Phase1WriteRequest"/> instances from repository walk results using tree-sitter parsing and chunking.
/// </summary>
public sealed class WriteRequestBuilder(ChunkerDispatcher chunkerDispatcher, ILogger<WriteRequestBuilder> logger) : IWriteRequestBuilder
{
    /// <summary>
    /// Builds write requests for all processable files in the walk result.
    /// </summary>
    /// <param name="repo">The repository being indexed.</param>
    /// <param name="walkResult">The result of the repository walk.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary mapping file paths to their write requests.</returns>
    public async Task<IReadOnlyDictionary<string, Phase1WriteRequest>> BuildAsync(
        Repo repo,
        WalkResult walkResult,
        CancellationToken cancellationToken = default,
        Action<string>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(walkResult);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, Phase1WriteRequest> requests = new(StringComparer.Ordinal);

        int total = walkResult.Files.Count(static f => f.Status != WalkedFileStatus.Deleted && f.Language != Language.Unknown);
        int processed = 0;

        foreach (WalkedFile file in walkResult.Files)
        {
            if (file.Status == WalkedFileStatus.Deleted || file.Language == Language.Unknown)
            {
                continue;
            }

            try
            {
                string filePath = Path.Combine(repo.LocalPath, file.Path);
                string source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

                IReadOnlyList<Chunk> chunks = await chunkerDispatcher.ChunkAsync(
                    new ChunkerInput(file.Path, file.Language, source),
                    cancellationToken).ConfigureAwait(false);

                SourceFile sourceFile = new()
                {
                    Id = HydrationIds.StableGuid($"file:{repo.Id}:{file.Path}"),
                    RepoId = repo.Id,
                    ProjectId = Guid.Empty,
                    Path = file.Path,
                    Language = LanguageName(file.Language),
                    ContentHash = ComputeHash(source),
                };

                Phase1WriteRequest request = new(
                    sourceFile,
                    Module: null,
                    chunks,
                    new Dictionary<string, SymbolSummary>(StringComparer.Ordinal),
                    []);

                requests[file.Path] = request;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process file '{FilePath}' ({Language}). Skipping.", file.Path, file.Language);
            }

            processed++;
            if (processed % 25 == 0 || processed == total)
            {
                onProgress?.Invoke($"Parsed {processed}/{total} files");
            }
        }

        return requests;
    }

    private static string LanguageName(Language language)
        => language switch
        {
            Language.CSharp => "csharp",
            Language.TypeScript => "typescript",
            Language.Tsx => "tsx",
            Language.JavaScript => "javascript",
            Language.Jsx => "jsx",
            Language.Python => "python",
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported language."),
        };

    private static string ComputeHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
