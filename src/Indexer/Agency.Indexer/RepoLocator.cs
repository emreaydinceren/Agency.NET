namespace Agency.Indexer;

/// <summary>
/// Finds the repository an invocation belongs to by walking up from a folder, the way <c>git</c> finds its repository:
/// the nearest ancestor holding <c>.agency-index.json</c> or a <c>.git</c> entry.
/// </summary>
internal static class RepoLocator
{
    /// <summary>The name of the repo-level config file.</summary>
    public const string ConfigFileName = ".agency-index.json";

    /// <summary>Returns the nearest <see cref="ConfigFileName"/> at or above <paramref name="start"/>, or <see langword="null"/>.</summary>
    public static string? FindConfig(string start)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, ConfigFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the folder of the nearest <see cref="ConfigFileName"/> or <c>.git</c> entry (file or folder) at or above
    /// <paramref name="start"/>, whichever is met first; <paramref name="start"/> itself when there is neither.
    /// </summary>
    public static string FindRoot(string start)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ConfigFileName))
                || Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        return Path.GetFullPath(start);
    }
}
