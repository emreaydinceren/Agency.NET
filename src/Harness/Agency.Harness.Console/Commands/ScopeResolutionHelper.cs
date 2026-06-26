using Agency.Harness;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class ScopeResolutionHelper
{
    public static (string? sessionId, string? projectId) Resolve(IProjectSessionState state)
    {
        IReadOnlyList<string> projects = state.LoadedProjects;

        if (projects.Count == 1)
        {
            return (null, projects[0]);
        }

        var choices = new List<string> { "Global", "Session" };
        foreach (string p in projects)
        {
            choices.Add($"Project: {p}");
        }

        choices.Add("Project: <new name>");

        string choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select ingestion scope:[/]")
                .AddChoices(choices));

        if (choice == "Global")
        {
            return (null, null);
        }

        if (choice == "Session")
        {
            return (state.SessionId, null);
        }

        if (choice == "Project: <new name>")
        {
            string name = AnsiConsole.Ask<string>("Project name:");
            return (null, name);
        }

        string projectName = choice["Project: ".Length..];
        return (null, projectName);
    }
}
