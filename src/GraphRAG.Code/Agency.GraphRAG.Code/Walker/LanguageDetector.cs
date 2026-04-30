namespace Agency.GraphRAG.Code.Walker;

/// <summary>
/// Detects a file language from its path and optional source content.
/// </summary>
public static class LanguageDetector
{
    /// <summary>
    /// Detects the language for a source file.
    /// </summary>
    /// <param name="path">The file path or file name.</param>
    /// <param name="source">The optional file contents used for shebang fallback.</param>
    /// <returns>The detected <see cref="Language"/>.</returns>
    public static Language Detect(string path, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string extension = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension))
        {
            return extension.ToLowerInvariant() switch
            {
                ".cs" => Language.CSharp,
                ".ts" => Language.TypeScript,
                ".tsx" => Language.Tsx,
                ".js" => Language.JavaScript,
                ".jsx" => Language.Jsx,
                ".py" => Language.Python,
                _ => DetectFromShebang(source),
            };
        }

        return DetectFromShebang(source);
    }

    private static Language DetectFromShebang(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Language.Unknown;
        }

        string firstLine = ReadFirstLine(source).Trim();
        if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            return Language.Unknown;
        }

        string shebang = firstLine.ToLowerInvariant();
        if (shebang.Contains("python", StringComparison.Ordinal))
        {
            return Language.Python;
        }

        if (shebang.Contains("ts-node", StringComparison.Ordinal) || shebang.Contains("tsx", StringComparison.Ordinal))
        {
            return Language.TypeScript;
        }

        if (shebang.Contains("node", StringComparison.Ordinal) ||
            shebang.Contains("bun", StringComparison.Ordinal) ||
            shebang.Contains("deno", StringComparison.Ordinal))
        {
            return Language.JavaScript;
        }

        return Language.Unknown;
    }

    private static string ReadFirstLine(string source)
    {
        int newlineIndex = source.IndexOfAny(['\r', '\n']);
        return newlineIndex >= 0 ? source[..newlineIndex] : source;
    }
}
