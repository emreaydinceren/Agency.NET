using Anthropic.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Agentic.Console.Commands;

internal static class CommandRegistery
{
    private static List<Command> commands = [];

    internal static IReadOnlyList<Command> Commands => commands;

    static CommandRegistery()
    {
        commands.Add(new Command("/exit", "Exit the current chat session.")
        {
            Execute = (_, _) => Task.FromResult(CommandContinuation.ExitSession)
        });

        commands.Add(new Command("/help", "Show help information.")
        {
            Execute = (_, _) => Task.FromResult(CommandContinuation.Continue)
        });

        commands.Add(new Command("/model", "Show model picker.")
        {
            Execute = (_, session) => ModelsCommand.RunSelectModelCommandAsync(session)
        });
    }
}

internal class ModelsCommand
{
    public static async Task<CommandContinuation> RunSelectModelCommandAsync(ConsoleChatSession session)
    {
        var models = session.ServiceProvider.GetRequiredService<Models>();
        var results = await models.GetAllAsync();

        List<string[]> modelRows = [];

        foreach (var result in results)
        {
            foreach (var model in result)
            {
                modelRows.Add(new string[] { result.Key.Name, model.Name });
            }
        }

        int pickerTop = System.Console.CursorTop;
        int pickerLeft = System.Console.CursorLeft;

        ConsolePicker.Show(modelRows, 0, pickerTop, pickerLeft, "Select Model", "Switch between models");

        return CommandContinuation.Continue;
    }
}