using System.Text;

namespace Agency.Harness.Instructions;

/// <summary>
/// Pure function that renders an <see cref="InstructionContext"/> to a formatted string
/// suitable for injection into the first user message. Provides provenance information
/// indicating the source and category of each instruction file.
/// </summary>
public static class InstructionRenderer
{
    /// <summary>
    /// Renders the complete instruction block string for injection into the first user message.
    /// Returns an empty string if no instructions are present.
    /// </summary>
    /// <param name="ctx">The resolved instruction context.</param>
    /// <returns>The formatted instruction block with provenance tags, or empty string if no sources.</returns>
    public static string Build(InstructionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ctx.HasSources)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<project-instructions>");

        foreach (InstructionSource source in ctx.Sources)
        {
            string provenance = source.Kind switch
            {
                InstructionSourceKind.RepoRoot => "(project instructions, checked into the repo)",
                InstructionSourceKind.Ancestor => "(ancestor directory instructions)",
                InstructionSourceKind.Mcp => $"(via MCP server '{source.McpServer}')",
                _ => "(instructions)"
            };

            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Contents of {source.Location} {provenance}:");
            sb.AppendLine(source.Content);
            sb.AppendLine();
        }

        sb.AppendLine("</project-instructions>");
        return sb.ToString();
    }
}
