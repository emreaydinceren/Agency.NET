using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

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

        ConsolePicker.Show(prompt =>
        {
            foreach (var result in results)
            {
                prompt.AddChoiceGroup<string>(result.Key.Name, result.Select(m => m.Name));
            }
            return prompt;
        }
        , "Select Model", "Switch between models");

        return CommandContinuation.Continue;
    }
}