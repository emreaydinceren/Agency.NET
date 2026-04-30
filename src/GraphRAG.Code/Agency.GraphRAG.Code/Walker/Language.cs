namespace Agency.GraphRAG.Code.Walker;

/// <summary>
/// Represents the supported source-code languages for repository walking.
/// </summary>
public enum Language
{
    /// <summary>
    /// The language could not be determined.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// C# source code.
    /// </summary>
    CSharp,

    /// <summary>
    /// TypeScript source code.
    /// </summary>
    TypeScript,

    /// <summary>
    /// TSX source code.
    /// </summary>
    Tsx,

    /// <summary>
    /// JavaScript source code.
    /// </summary>
    JavaScript,

    /// <summary>
    /// JSX source code.
    /// </summary>
    Jsx,

    /// <summary>
    /// Python source code.
    /// </summary>
    Python,
}
