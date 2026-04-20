using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Agency.Agentic.Console.Commands;

internal class ModelsCommand
{
    public static async Task<CommandContinuation> RunSelectModelCommandAsync(ConsoleChatSession session)
    {
        var models = session.ServiceProvider.GetRequiredService<Models>();
        var agentFactory = session.ServiceProvider.GetRequiredService<IAgentFactory>();
        var results = await models.GetAllAsync();

        var (selectedClient, selectedModel) = ConsolePicker.Show<(string,string)>(prompt =>
        {
            foreach (var result in results)
            {
                prompt.AddChoiceGroup<(string, string)>(new (string.Empty, result.Key.Name), result.Select(m => (result.Key.Name, m.Id)));
            }
            return prompt;
        },

        itemToStringConverter: item => item.Item2,
        moreChoicesText: "More models available...",
        title: "Select Model",
        searchPlaceholderText: "Switch between models");

        if (selectedClient is not null && selectedModel is not null)
        {
            var agent = agentFactory.CreateAgent(selectedClient, selectedModel, stream: true);
            session.SetAgent(agent);
            AnsiConsole.MarkupLine($"[green]⎿ Switched to model:[/] [yellow]{selectedModel}[/] from client [yellow]{selectedClient}[/]");
        }

        return CommandContinuation.Continue;
    }
}