using Agency.Harness;
using Agency.Harness.Console.Services;
using Agency.VectorStore.Common;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class ProjectsCommand
{
    public static Task<CommandContinuation> LoadAsync(string input, ConsoleChatSession session)
    {
        string name = input.Length > "/projects-load".Length
            ? input["/projects-load".Length..].Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = AnsiConsole.Ask<string>("Project name:");
        }

        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();
        state.LoadProject(name);
        AnsiConsole.MarkupLine($"[green]Project '{name}' loaded.[/]");

        DocumentContextHydrationService hydration =
            session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
        hydration.MarkDirty();

        return Task.FromResult(CommandContinuation.Continue);
    }

    public static Task<CommandContinuation> UnloadAsync(string input, ConsoleChatSession session)
    {
        string name = input.Length > "/projects-unload".Length
            ? input["/projects-unload".Length..].Trim()
            : string.Empty;

        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();

        if (string.IsNullOrWhiteSpace(name))
        {
            if (state.LoadedProjects.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No projects currently loaded.[/]");
                return Task.FromResult(CommandContinuation.Continue);
            }

            name = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select project to unload:")
                    .AddChoices(state.LoadedProjects));
        }

        state.UnloadProject(name);
        AnsiConsole.MarkupLine($"[green]Project '{name}' unloaded.[/]");

        DocumentContextHydrationService hydration =
            session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
        hydration.MarkDirty();

        return Task.FromResult(CommandContinuation.Continue);
    }

    public static async Task<CommandContinuation> ListAsync(ConsoleChatSession session)
    {
        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();
        IVectorStore store = session.ServiceProvider.GetRequiredService<IVectorStore>();

        IReadOnlyList<string> allProjects = await store.ListProjectsAsync(state.UserId);
        IReadOnlyList<string> loaded = state.LoadedProjects;

        if (allProjects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No projects found in the vector store.[/]");
            return CommandContinuation.Continue;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Project");
        table.AddColumn("Status");

        foreach (string project in allProjects)
        {
            bool isLoaded = loaded.Contains(project, StringComparer.OrdinalIgnoreCase);
            table.AddRow(project, isLoaded ? "[green]● loaded[/]" : "[grey]○ available[/]");
        }

        AnsiConsole.Write(table);
        return CommandContinuation.Continue;
    }
}
