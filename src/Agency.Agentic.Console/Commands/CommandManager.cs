namespace Agency.Agentic.Console.Commands;

internal class CommandManager(IEnumerable<Command> initialCommands)
{
    private readonly List<Command> _commands = new (initialCommands);

    public IReadOnlyList<Command> GetCommands() => _commands;

    public CommandContinuation ExecuteCommand(string commandText)
    {
        var command = this._commands.FirstOrDefault(c => c.CommandText.StartsWith(commandText, StringComparison.OrdinalIgnoreCase));

        if (command is null)
        {
            return CommandContinuation.Continue;
        }

        return command.Execute(commandText);
    }
}
