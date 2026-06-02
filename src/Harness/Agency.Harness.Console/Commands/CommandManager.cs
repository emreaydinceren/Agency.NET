namespace Agency.Harness.Console.Commands;

internal class CommandManager(IEnumerable<Command> initialCommands, ConsoleChatSession session)
{
    private readonly List<Command> _commands = new (initialCommands);

    public IReadOnlyList<Command> GetCommands() => _commands;

    public Task<CommandContinuation> ExecuteCommandAsync(string commandText)
    {
        var command = this._commands.FirstOrDefault(c => commandText.StartsWith(c.CommandText, StringComparison.OrdinalIgnoreCase));

        if (command is null)
        {
            return Task.FromResult(CommandContinuation.Continue);
        }

        return command.Execute(commandText, session);
    }
}
