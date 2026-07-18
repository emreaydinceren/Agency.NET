using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal class ModelsCommand
{
    public static async Task<CommandContinuation> RunSelectModelCommandAsync(string input, ConsoleChatSession session)
    {
        var models = session.ServiceProvider.GetRequiredService<Models>();
        var agentFactory = session.ServiceProvider.GetRequiredService<IAgentFactory>();
        var results = await models.GetAllAsync();

        var items = results
            .SelectMany(result => result.Select(m => new ConsolePickerItem<(string, string)>(
                value: (result.Key.Name, m.Id),
                displayText: m.Id,
                searchText: m.Id,
                groupLabel: result.Key.Name)))
            .ToList();

        string requestedName = input.Length > "/model".Length
            ? input["/model".Length..].Trim()
            : string.Empty;

        var (selectedClient, selectedModel) = ResolveByName(items, requestedName) ?? ConsolePicker.Show(
            items,
            title: "Select Model",
            moreChoicesText: "More models available...",
            filterPlaceholderText: "Switch between models");

        if (selectedClient is not null && selectedModel is not null)
        {
            var agent = agentFactory.CreateAgent(selectedClient, selectedModel);
            session.SetAgent(agent);

            var environment = session.ServiceProvider.GetRequiredService<IHostEnvironment>();
            if (environment.IsEnvironment("Test") == false)
            {
                var configuration = session.ServiceProvider.GetRequiredService<IConfiguration>();
                string appSettingsPath = Path.Combine(environment.ContentRootPath, "appsettings.json");
                DefaultModelConfiguration.Persist(configuration, appSettingsPath, selectedClient, selectedModel);
            }

            AnsiConsole.MarkupLine($"[green]⎿ Switched to model:[/] [yellow]{selectedModel}[/] from client [yellow]{selectedClient}[/]");
        }

        return CommandContinuation.Continue;
    }

    /// <summary>
    /// Resolves <paramref name="requestedName"/> against the discovered models' ids: an exact
    /// (case-insensitive) match wins, falling back to a startsWith match when unambiguous
    /// (mirroring the picker's own filter semantics). Returns <see langword="null"/> — leaving
    /// the caller to fall back to the interactive picker — when no name was requested, or when
    /// the match is missing or ambiguous.
    /// </summary>
    private static (string, string)? ResolveByName(List<ConsolePickerItem<(string, string)>> items, string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return null;
        }

        var matches = items
            .Where(item => item.SearchText.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            matches = items
                .Where(item => item.SearchText.StartsWith(requestedName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matches.Count == 1)
        {
            return matches[0].Value;
        }

        string reason = matches.Count == 0 ? "No model matches" : "Multiple models match";
        AnsiConsole.MarkupLine($"[yellow]{reason} '{Markup.Escape(requestedName)}'; showing the picker.[/]");
        return null;
    }
}