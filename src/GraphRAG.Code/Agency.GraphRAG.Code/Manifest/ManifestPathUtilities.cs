namespace Agency.GraphRAG.Code.Manifest;

internal static class ManifestPathUtilities
{
    public static string NormalizeRelativePath(string rootPath, string fullPath)
    {
        return Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}
