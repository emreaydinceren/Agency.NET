namespace Agency.Harness.Instructions;

/// <summary>
/// Specifies the source category of resolved instructions.
/// </summary>
public enum InstructionSourceKind
{
    /// <summary>Instructions found at the repository root (the directory containing .git).</summary>
    RepoRoot,

    /// <summary>Instructions found in an ancestor directory between working directory and repo root.</summary>
    Ancestor,

    /// <summary>Instructions discovered from an MCP server's resources capability.</summary>
    Mcp
}

/// <summary>
/// Resolves user-authored instruction files (AGENTS.md, etc.) from the project
/// and its MCP servers for injection into the first user message.
/// </summary>
public interface IInstructionResolver
{
    /// <summary>
    /// Resolves instruction files from the working directory and configured locations.
    /// Returns an empty context if no instructions are found.
    /// </summary>
    /// <param name="workingDirectory">The project root directory to search in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved instruction context with sources and content.</returns>
    ValueTask<InstructionContext> ResolveAsync(string workingDirectory, CancellationToken ct = default);
}

/// <summary>
/// Represents a single instruction source with its content and provenance information.
/// </summary>
public sealed record InstructionSource(
    string Location,
    string Content,
    InstructionSourceKind Kind,
    string? McpServer = null);

/// <summary>
/// Contains resolved instruction files ready for injection into the first user message.
/// </summary>
public sealed record InstructionContext(IReadOnlyList<InstructionSource> Sources)
{
    /// <summary>Gets an empty instruction context (no sources found).</summary>
    public static InstructionContext Empty => new([]);

    /// <summary>Gets whether any instructions were resolved.</summary>
    public bool HasSources => Sources.Count > 0;
}
