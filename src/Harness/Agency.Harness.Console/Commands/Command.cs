namespace Agency.Harness.Console.Commands;

internal class Command(string Name, string Description, string? ArgumentHint = null)
{
    public string CommandText { get; } = Name;

    public string Description { get; } = Description;

    /// <summary>
    /// Gets the optional autocomplete hint for the arguments expected by this command
    /// (e.g. <c>"&lt;query&gt;"</c>). Displayed in the <c>/</c> command picker when non-<see langword="null"/>.
    /// </summary>
    public string? ArgumentHint { get; } = ArgumentHint;

    public required Func<string, ConsoleChatSession, Task<CommandContinuation>> Execute { get; set; }
}