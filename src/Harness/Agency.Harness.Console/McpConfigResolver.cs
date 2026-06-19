using Agency.Harness.Tools;

namespace Agency.Harness.Console;

/// <summary>
/// Expands path placeholders in MCP server configuration so the committed
/// <c>appsettings.json</c> stays portable across machines, drives, operating systems
/// and build configurations. Two tokens are supported:
/// <list type="bullet">
///   <item><description><c>${RepoRoot}</c> — the repository root (the directory containing <c>.git</c>).</description></item>
///   <item><description><c>${Configuration}</c> — the build configuration (e.g. <c>Debug</c> or <c>Release</c>) the console was built in.</description></item>
/// </list>
/// </summary>
internal static class McpConfigResolver
{
    private const string RepoRootToken = "${RepoRoot}";
    private const string ConfigurationToken = "${Configuration}";

    /// <summary>
    /// Substitutes the <c>${RepoRoot}</c> and <c>${Configuration}</c> tokens in every server's
    /// <see cref="McpServerConfig.Command"/>, <see cref="McpServerConfig.Arguments"/> and
    /// <see cref="McpServerConfig.EnvironmentVariables"/> values, mutating <paramref name="options"/> in place.
    /// </summary>
    public static void Expand(McpClientOptions options, string repoRoot, string configuration)
    {
        foreach (McpServerConfig server in options.Servers)
        {
            server.Command = Substitute(server.Command, repoRoot, configuration);

            if (server.Arguments is { } args)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = Substitute(args[i], repoRoot, configuration) ?? args[i];
                }
            }

            if (server.EnvironmentVariables is { } env)
            {
                foreach (string key in env.Keys.ToArray())
                {
                    env[key] = Substitute(env[key], repoRoot, configuration);
                }
            }
        }
    }

    private static string? Substitute(string? value, string repoRoot, string configuration) =>
        value?
            .Replace(RepoRootToken, repoRoot, StringComparison.Ordinal)
            .Replace(ConfigurationToken, configuration, StringComparison.Ordinal);

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> until it finds the directory that contains a
    /// <c>.git</c> entry, returning that directory as the repository root. Returns <c>null</c> when
    /// no such ancestor exists (e.g. when the app is published outside a working tree).
    /// </summary>
    public static string? FindRepoRoot(string startDirectory)
    {
        DirectoryInfo? dir = new(startDirectory);
        while (dir is not null)
        {
            if (Path.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Derives the build configuration (e.g. <c>Debug</c>/<c>Release</c>) from a <c>bin</c> output path
    /// such as <c>.../bin/Debug/net10.0/</c>. Falls back to <c>Debug</c> when the path has no <c>bin</c> segment.
    /// </summary>
    public static string ResolveConfiguration(string baseDirectory)
    {
        string trimmed = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] segments = trimmed.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        int binIdx = Array.LastIndexOf(segments, "bin");
        return binIdx >= 0 && binIdx + 1 < segments.Length ? segments[binIdx + 1] : "Debug";
    }
}
