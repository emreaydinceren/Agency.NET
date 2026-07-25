using Agency.Harness.Console.Services;
using Agency.Ingestion;
using Agency.VectorStore.Common;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class AddFileCommand
{
    public static async Task<CommandContinuation> RunAsync(string input, ConsoleChatSession session)
    {
        string filePath = input.Length > "/add-file".Length
            ? input["/add-file".Length..].Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            filePath = AnsiConsole.Ask<string>("File path:");
        }

        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {filePath}");
            return CommandContinuation.Continue;
        }

        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();
        IVectorStore store = session.ServiceProvider.GetRequiredService<IVectorStore>();

        string normalizedPath = Path.GetFullPath(filePath);
        IReadOnlyList<DocumentInfo> existing = await store.ListDocumentsAsync(
            state.UserId, state.SessionId, state.LoadedProjects);

        bool alreadyIngested = existing.Any(d =>
            string.Equals(Path.GetFullPath(d.SourceFile), normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (alreadyIngested && !AnsiConsole.Confirm("File already ingested. Re-ingest?", defaultValue: false))
        {
            return CommandContinuation.Continue;
        }

        (string? sessionId, string? projectId) = ScopeResolutionHelper.Resolve(state);

        IngestionCommandService ingestor = session.ServiceProvider.GetRequiredService<IngestionCommandService>();

        IngestionResult result = new(Succeeded: 0, Failed: 0);
        await AnsiConsole.Status().StartAsync("Ingesting...", async ctx =>
        {
            result = await ingestor.IngestFileAsync(filePath, state.UserId, sessionId, projectId);
            ctx.Status($"Done — {result.Succeeded} chunk(s) ingested.");
        });

        AnsiConsole.MarkupLine($"[green]Ingested 1 file, {result.Succeeded} chunk(s).[/]");
        if (result.Failed > 0)
        {
            AnsiConsole.MarkupLine($"[red]{result.Failed} chunk(s) failed to ingest:[/]");
            foreach (string reason in result.FailureReasons ?? [])
            {
                AnsiConsole.MarkupLine($"[red]  - {reason.EscapeMarkup()}[/]");
            }
        }

        DocumentContextHydrationService hydration =
            session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
        hydration.MarkDirty();

        return CommandContinuation.Continue;
    }
}
