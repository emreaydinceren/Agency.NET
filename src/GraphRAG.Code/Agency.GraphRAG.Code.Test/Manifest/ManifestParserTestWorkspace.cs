namespace Agency.GraphRAG.Code.Test.Manifest;

/// <summary>
/// Creates an isolated manifest parser test workspace beneath the test output directory.
/// </summary>
internal sealed class ManifestParserTestWorkspace : IDisposable
{
    private readonly string _rootPath;

    private ManifestParserTestWorkspace(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string RootPath => _rootPath;

    public static ManifestParserTestWorkspace Create()
    {
        string rootPath = Path.Combine(AppContext.BaseDirectory, "ManifestParserTestWorkspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return new ManifestParserTestWorkspace(rootPath);
    }

    public string WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        string normalizedContent = content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        File.WriteAllText(fullPath, normalizedContent);
        return fullPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
