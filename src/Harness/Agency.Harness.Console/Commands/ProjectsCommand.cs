using Agency.Harness.Console.Services;
using Agency.VectorStore.Common;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class ProjectsCommand
{
    private const string ListCommandText = "/project-list";
    private const string LoadCommandText = "/project-load";
    private const string UnloadCommandText = "/project-unload";
    private const string CreateCommandText = "/project-create";
    private const string DeleteCommandText = "/project-delete";
    private const string ShowCommandText = "/project-show";

    public static Task<CommandContinuation> LoadAsync(string input, ConsoleChatSession session)
    {
        string name = input.Length > LoadCommandText.Length
            ? input[LoadCommandText.Length..].Trim()
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
        string name = input.Length > UnloadCommandText.Length
            ? input[UnloadCommandText.Length..].Trim()
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

    public static async Task<CommandContinuation> CreateAsync(string input, ConsoleChatSession session)
    {
        string raw = input.Length > CreateCommandText.Length
            ? input[CreateCommandText.Length..].Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = AnsiConsole.Ask<string>("Project name:");
        }

        if (!ProjectName.TryNormalize(raw, out string canonical, out string? error))
        {
            AnsiConsole.MarkupLine($"[red]Invalid project name. {error}[/]");
            return CommandContinuation.Continue;
        }

        IVectorStore store = session.ServiceProvider.GetRequiredService<IVectorStore>();
        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();

        bool created = await store.CreateProjectAsync(state.UserId, canonical);

        bool wasLoaded = state.LoadedProjects.Contains(canonical, StringComparer.OrdinalIgnoreCase);
        state.LoadProject(canonical);

        AnsiConsole.MarkupLine(created
            ? $"[green]Project '{canonical}' created and loaded.[/]"
            : $"[yellow]Project '{canonical}' already exists — loaded.[/]");

        if (!wasLoaded)
        {
            DocumentContextHydrationService hydration =
                session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
            hydration.MarkDirty();
        }

        return CommandContinuation.Continue;
    }

    public static async Task<CommandContinuation> DeleteAsync(string input, ConsoleChatSession session)
    {
        string raw = input.Length > DeleteCommandText.Length
            ? input[DeleteCommandText.Length..].Trim()
            : string.Empty;

        IVectorStore store = session.ServiceProvider.GetRequiredService<IVectorStore>();
        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();

        IReadOnlyList<string> knownProjects = await store.ListProjectsAsync(state.UserId);

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (knownProjects.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No projects found.[/]");
                return CommandContinuation.Continue;
            }

            raw = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select project to delete:")
                    .AddChoices(knownProjects));
        }

        string id;
        switch (Resolve(raw, knownProjects))
        {
            case ResolveResult.Invalid invalid:
                AnsiConsole.MarkupLine($"[red]Invalid project name. {invalid.Reason}[/]");
                return CommandContinuation.Continue;
            case ResolveResult.NotKnown notKnown:
                AnsiConsole.MarkupLine($"[yellow]Project '{notKnown.Canonical}' not found.[/]");
                return CommandContinuation.Continue;
            case ResolveResult.Known known:
                id = known.StoredId;
                break;
            default:
                throw new InvalidOperationException("Unreachable ResolveResult case.");
        }

        IReadOnlyList<DocumentInfo> docs = await GetProjectDocumentsAsync(store, state.UserId, id);

        if (!AnsiConsole.Confirm(
            $"Permanently delete project '{id}' and its {docs.Count} document(s)? This cannot be undone.",
            defaultValue: false))
        {
            return CommandContinuation.Continue;
        }

        int chunks = 0;
        await AnsiConsole.Status().StartAsync("Deleting…", async _ =>
        {
            chunks = await store.DeleteProjectAsync(state.UserId, id);
        });

        AnsiConsole.MarkupLine($"[green]Deleted project '{id}' — {chunks} chunk(s) removed.[/]");

        if (state.LoadedProjects.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            state.UnloadProject(id);
            AnsiConsole.MarkupLine($"[yellow]Project '{id}' was loaded in this session and has been unloaded.[/]");
        }

        DocumentContextHydrationService hydration =
            session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
        hydration.MarkDirty();

        IReadOnlyList<string> remainingProjects = await store.ListProjectsAsync(state.UserId);
        if (remainingProjects.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Project '{id}' reappeared — an ingest may be running concurrently. Re-run /project-delete.[/]");
        }

        return CommandContinuation.Continue;
    }

    public static async Task<CommandContinuation> ShowAsync(string input, ConsoleChatSession session)
    {
        string raw = input.Length > ShowCommandText.Length
            ? input[ShowCommandText.Length..].Trim()
            : string.Empty;

        IVectorStore store = session.ServiceProvider.GetRequiredService<IVectorStore>();
        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();

        IReadOnlyList<string> knownProjects = await store.ListProjectsAsync(state.UserId);

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (knownProjects.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No projects found.[/]");
                return CommandContinuation.Continue;
            }

            raw = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select project to show:")
                    .AddChoices(knownProjects));
        }

        string id;
        switch (Resolve(raw, knownProjects))
        {
            case ResolveResult.Invalid invalid:
                AnsiConsole.MarkupLine($"[red]Invalid project name. {invalid.Reason}[/]");
                return CommandContinuation.Continue;
            case ResolveResult.NotKnown notKnown:
                AnsiConsole.MarkupLine($"[yellow]Project '{notKnown.Canonical}' not found.[/]");
                return CommandContinuation.Continue;
            case ResolveResult.Known known:
                id = known.StoredId;
                break;
            default:
                throw new InvalidOperationException("Unreachable ResolveResult case.");
        }

        IReadOnlyList<DocumentInfo> docs = await GetProjectDocumentsAsync(store, state.UserId, id);

        if (docs.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Project '{id}' has no documents ingested yet. Use /add-file or /add-folder to ingest into it.[/]");
            return CommandContinuation.Continue;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Document");
        table.AddColumn("Scope");

        foreach (DocumentInfo doc in docs)
        {
            table.AddRow(doc.SourceFile, $"project:{id}");
        }

        AnsiConsole.Write(table);

        string loadedGlyph = state.LoadedProjects.Contains(id, StringComparer.OrdinalIgnoreCase)
            ? "[green]● loaded[/]"
            : "[grey]○ not loaded[/]";
        AnsiConsole.MarkupLine($"{docs.Count} document(s) in project '{id}' · {loadedGlyph} in this session");

        return CommandContinuation.Continue;
    }

    /// <summary>
    /// The outcome of resolving operator-supplied input against the set of known project ids
    /// (Spec §8.1 Step 3). Distinguishes three cases that <c>/project-delete</c> and
    /// <c>/project-show</c> must each handle differently: a name that failed validation, a name
    /// that validated but matches no known project, and a name that matches a known project —
    /// carrying the id exactly as it is stored, which may differ in case from the canonical form.
    /// </summary>
    internal abstract record ResolveResult
    {
        /// <summary>The input resolved to a known project. <paramref name="StoredId"/> is the
        /// spelling already present in <c>knownProjects</c> — not the canonicalised form — so
        /// callers can address the project's actual rows (e.g. a legacy mixed-case id).</summary>
        internal sealed record Known(string StoredId) : ResolveResult;

        /// <summary>The input validated but does not match any known project.
        /// <paramref name="Canonical"/> is the canonical form, i.e. what
        /// <c>/project-create</c> would declare.</summary>
        internal sealed record NotKnown(string Canonical) : ResolveResult;

        /// <summary>The input failed <see cref="ProjectName.TryNormalize"/>. <paramref name="Reason"/>
        /// is the validation error, surfaced verbatim.</summary>
        internal sealed record Invalid(string Reason) : ResolveResult;
    }

    internal static ResolveResult Resolve(string raw, IReadOnlyList<string> knownProjects)
    {
        if (!ProjectName.TryNormalize(raw, out string canonical, out string? error))
        {
            return new ResolveResult.Invalid(error!);
        }

        string? exact = knownProjects.FirstOrDefault(p => p == canonical);
        if (exact is not null)
        {
            return new ResolveResult.Known(exact);
        }

        string? ci = knownProjects.FirstOrDefault(p => p.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        if (ci is not null)
        {
            return new ResolveResult.Known(ci);
        }

        return new ResolveResult.NotKnown(canonical);
    }

    internal static async Task<IReadOnlyList<DocumentInfo>> GetProjectDocumentsAsync(
        IVectorStore store, string userId, string projectId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentInfo> docs =
            await store.ListDocumentsAsync(userId, sessionId: null, projectIds: [projectId], ct);
        return docs.Where(d => d.ProjectId == projectId).ToList();
    }
}
