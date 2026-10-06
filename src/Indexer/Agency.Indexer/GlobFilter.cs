using System.Text;
using System.Text.RegularExpressions;

namespace Agency.Indexer;

/// <summary>
/// Matches paths, relative to an index root, against gitignore-style globs: <c>*</c> and <c>?</c> stay inside a path segment,
/// <c>**</c> crosses segments, a pattern without a <c>/</c> matches a file or folder of that name at any depth, and a pattern
/// that matches a folder matches everything beneath it. Used for <c>Exclude</c> and for <c>search --path</c>.
/// </summary>
internal sealed class GlobFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex[] _patterns;

    /// <summary>Creates a filter from <paramref name="globs"/>; blank entries are ignored.</summary>
    public GlobFilter(IEnumerable<string>? globs) =>
        this._patterns = (globs ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(ToRegex).ToArray();

    /// <summary>Gets a value indicating whether the filter has no patterns (and so matches nothing).</summary>
    public bool IsEmpty => this._patterns.Length == 0;

    /// <summary>
    /// Returns whether <paramref name="relativePath"/> (any separator) or one of its parent folders matches a pattern.
    /// </summary>
    public bool Matches(string relativePath)
    {
        if (this.IsEmpty)
        {
            return false;
        }

        string path = relativePath.Replace('\\', '/').Trim('/');
        while (path.Length > 0)
        {
            foreach (Regex pattern in this._patterns)
            {
                if (pattern.IsMatch(path))
                {
                    return true;
                }
            }

            int slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }

        return false;
    }

    private static Regex ToRegex(string glob)
    {
        string g = glob.Trim().Replace('\\', '/');
        while (g.StartsWith("./", StringComparison.Ordinal))
        {
            g = g[2..];
        }

        g = g.Trim('/');
        if (!g.Contains('/', StringComparison.Ordinal))
        {
            g = "**/" + g;
        }

        var regex = new StringBuilder("^");
        int i = 0;
        while (i < g.Length)
        {
            char c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                bool followedBySlash = i + 2 < g.Length && g[i + 2] == '/';
                regex.Append(followedBySlash ? "(?:.*/)?" : ".*");
                i += followedBySlash ? 3 : 2;
                continue;
            }

            regex.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(c.ToString()),
            });
            i++;
        }

        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }
}
