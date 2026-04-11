namespace Agency.Ingestion.FileSystem;

using System.Runtime.CompilerServices;

/// <summary>
/// Loads a single file as a <see cref="Document"/>.
/// </summary>
public sealed class FileLoader(string filePath) : IDocumentLoader
{
    private readonly string _filePath = filePath
        ?? throw new ArgumentNullException(nameof(filePath));

    /// <inheritdoc/>
    public async IAsyncEnumerable<Document> LoadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string content = await File.ReadAllTextAsync(this._filePath, ct);
        yield return BuildDocument(this._filePath, content);
    }

    internal static Document BuildDocument(string filePath, string content)
    {
        return new Document(
            content,
            filePath,
            new Dictionary<string, object>
            {
                ["file_path"] = filePath,
                ["file_name"] = Path.GetFileName(filePath),
                ["file_extension"] = Path.GetExtension(filePath),
            });
    }
}
