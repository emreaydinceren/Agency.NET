using Agency.Harness.Skills;

namespace Agency.Harness.Console.Commands;

internal static class CommandRegistry
{
    private static readonly List<Command> commands = [];

    internal static IReadOnlyList<Command> Commands => commands;

    internal static void RegisterCommand(
        string commandText,
        string description,
        Func<string, ConsoleChatSession, CommandContinuation> executeFunc,
        string? argumentHint = null)
    {
        commands.Add(new Command(commandText, description, argumentHint)
        {
            Execute = (text, session) => Task.FromResult(executeFunc(text, session)),
        });
    }

    internal static void RegisterAsyncCommand(
       string commandText,
       string description,
       Func<string, ConsoleChatSession, Task<CommandContinuation>> executeFunc,
       string? argumentHint = null)
    {
        commands.Add(new Command(commandText, description, argumentHint)
        {
            Execute = executeFunc
        });
    }

    /// <summary>
    /// Registers all user-invocable skills from <paramref name="catalog"/> as <c>/skill-name</c> commands.
    /// Each registered command renders the skill body via <see cref="SkillRenderer"/> and submits
    /// the result as a user turn. Skills with <see cref="Skill.UserInvocable"/> set to
    /// <see langword="false"/> are skipped regardless of <see cref="Skill.DisableModelInvocation"/>.
    /// </summary>
    /// <param name="catalog">The skill catalog to source skills from.</param>
    internal static void RegisterSkillCommands(ISkillCatalog catalog)
    {
        foreach (Skill skill in catalog.List())
        {
            if (!skill.UserInvocable)
            {
                continue;
            }

            // Capture loop variable so the closure is correct for each skill.
            Skill captured = skill;
            string commandText = $"/{captured.Name}";

            commands.Add(new Command(commandText, captured.Description, captured.ArgumentHint)
            {
                Execute = (text, session) =>
                {
                    // Strip the "/name" prefix to extract the argument string.
                    string argsString = text.Length > commandText.Length
                        ? text[commandText.Length..].TrimStart()
                        : string.Empty;

                    string renderedBody = SkillRenderer.Render(captured, argsString, sessionId: string.Empty);
                    return session.SubmitSkillTurnAsync(renderedBody);
                }
            });
        }
    }

    static CommandRegistry()
    {
        RegisterCommand("/clear", "Clear the console.", (_, _) => CommandContinuation.Clear);
        RegisterCommand("/exit", "Exit the current chat session.", (_, _) => CommandContinuation.ExitSession);
        RegisterCommand("/quit", "Exit the current chat session.", (_, _) => CommandContinuation.ExitSession);
        RegisterCommand("/help", "Show help information.", (_, _) => HelpCommand.Run());
        RegisterAsyncCommand("/model", "Show model picker, or switch directly by passing a model name.", ModelsCommand.RunSelectModelCommandAsync, argumentHint: "[model name]");
        RegisterCommand("/dump-context", "Print the full context sent to the model (not added to history).",
            (_, session) => DumpContextCommand.Run(session));
        RegisterAsyncCommand("/add-file", "Ingest a file into the vector store.", AddFileCommand.RunAsync, argumentHint: "<path>");
        RegisterAsyncCommand("/add-folder", "Ingest all files in a folder into the vector store.", AddFolderCommand.RunAsync, argumentHint: "<path>");
        RegisterAsyncCommand("/project-load", "Load a project into the session context.", ProjectsCommand.LoadAsync, argumentHint: "<name>");
        RegisterAsyncCommand("/project-unload", "Unload a project from the session context.", ProjectsCommand.UnloadAsync, argumentHint: "<name>");
        RegisterAsyncCommand("/project-list", "List all projects in the vector store.", (_, session) => ProjectsCommand.ListAsync(session));
        RegisterAsyncCommand("/project-create", "Create (and load) a project so it exists before any document is ingested into it.", ProjectsCommand.CreateAsync, argumentHint: "<name>");
        RegisterAsyncCommand("/project-delete", "Permanently delete a project and every document ingested into it.", ProjectsCommand.DeleteAsync, argumentHint: "<name>");
        RegisterAsyncCommand("/project-show", "List the documents held in one project, without loading it.", ProjectsCommand.ShowAsync, argumentHint: "<name>");
    }
}
