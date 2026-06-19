namespace Agency.Harness.Console.Commands;

internal static class CommandRegistry
{
    private static List<Command> commands = [];

    internal static IReadOnlyList<Command> Commands => commands;

    internal static void RegisterCommand(
        string commandText, 
        string description,
        Func<string, ConsoleChatSession, CommandContinuation> executeFunc)
    {
        commands.Add(new Command(commandText, description)
        {
            Execute = (text, session) => Task.FromResult(executeFunc(text, session)),
        });
    }

    internal static void RegisterAsyncCommand(
       string commandText,
       string description,
       Func<string, ConsoleChatSession, Task<CommandContinuation>> executeFunc)
    {
        commands.Add(new Command(commandText, description)
        {
            Execute = executeFunc
        });
    }

    static CommandRegistry()
    {
        RegisterCommand("/clear", "Clear the console.", (_, _) => CommandContinuation.Clear);
        RegisterCommand("/exit", "Exit the current chat session.", (_, _) => CommandContinuation.ExitSession);
        RegisterCommand("/quit", "Exit the current chat session.", (_, _) => CommandContinuation.ExitSession);
        RegisterCommand("/help", "Show help information.", (_, _) => CommandContinuation.Continue);
        RegisterAsyncCommand("/model", "Show model picker.", (_, session) => ModelsCommand.RunSelectModelCommandAsync(session));
        RegisterCommand("/dump-context", "Print the full context sent to the model (not added to history).",
            (_, session) => DumpContextCommand.Run(session));
    }
}
