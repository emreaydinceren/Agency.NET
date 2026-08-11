namespace Agency.Harness.Instructions;

/// <summary>
/// Configuration for the instruction resolver, bound from the <c>Instructions</c> section in appsettings.json.
/// </summary>
internal sealed class InstructionsOptions
{
    /// <summary>
    /// Gets or sets whether instruction resolution is enabled. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the ordered list of filenames to search for when resolving instructions.
    /// The first match per directory is used; searching continues with the next filename only
    /// if the current filename is not found.
    /// Defaults to ["AGENTS.md"].
    /// </summary>
    public string[] FallbackFilenames { get; set; } = ["AGENTS.md"];
}
