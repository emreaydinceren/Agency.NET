namespace Agency.Ingestion.FileSystem;

using System.Runtime.CompilerServices;

/// <summary>
/// Recursively enumerates files in a directory and loads each as a <see cref="Document"/>.
/// </summary>
public sealed class DirectoryLoader(
    string directoryPath,
    string searchPattern = "*.md") : IDocumentLoader
{
    private readonly string _directoryPath = directoryPath
        ?? throw new ArgumentNullException(nameof(directoryPath));

    private readonly string _searchPattern = searchPattern
        ?? throw new ArgumentNullException(nameof(searchPattern));

    /// <inheritdoc/>
    public async IAsyncEnumerable<Document> LoadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(
            this._directoryPath,
            this._searchPattern,
            SearchOption.AllDirectories);

        foreach (string filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            await foreach (Document doc in new FileLoader(filePath).LoadAsync(ct))
            {
                yield return doc;
            }
        }
    }
}
