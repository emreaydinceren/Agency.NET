namespace Agency.Agentic.Console.Commands;

internal static class CommandRegistery
{
    private static List<Command> commands = [];

    internal static IReadOnlyList<Command> Commands => commands;

    static CommandRegistery()
    {
        commands.Add(new Command("/exit", "Exit the current chat session.")
        {
            Execute = _ => CommandContinuation.ExitSession
        });

        commands.Add(new Command("/help", "Show help information.")
        {
            Execute = _ => CommandContinuation.Continue
        });

        commands.Add(new Command("/model", "Show model picker.")
        {
            Execute = _ => CommandContinuation.Continue
        });
    }
}