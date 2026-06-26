using Agency.Ingestion;
using Agency.Ingestion.FileSystem;
using Agency.VectorStore.Common;

namespace Agency.Harness.Console.Services;

internal sealed class IngestionCommandService(
    IVectorStore vectorStore,
    ITextSplitter textSplitter)
{
    public async Task<int> IngestFileAsync(
        string filePath,
        string userId,
        string? sessionId,
        string? projectId,
        CancellationToken ct = default)
    {
        var pipeline = new DefaultIngestionPipeline<string>(chunk => chunk.Content);
        IngestionResult result = await pipeline.ExecuteAsync(
            new FileLoader(filePath),
            textSplitter,
            vectorStore,
            userId,
            sessionId,
            projectId,
            ct);
        return result.Succeeded;
    }

    public async Task<int> IngestDirectoryAsync(
        string directoryPath,
        string searchPattern,
        string userId,
        string? sessionId,
        string? projectId,
        CancellationToken ct = default)
    {
        var pipeline = new DefaultIngestionPipeline<string>(chunk => chunk.Content);
        IngestionResult result = await pipeline.ExecuteAsync(
            new DirectoryLoader(directoryPath, searchPattern),
            textSplitter,
            vectorStore,
            userId,
            sessionId,
            projectId,
            ct);
        return result.Succeeded;
    }

    public static int CountFiles(string directoryPath, string searchPattern) =>
        Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories).Length;
}
