using Agency.Harness.Console.Configuration;
using Agency.Harness.Console.Services;
using Agency.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class AddFolderCommand
{
    public static async Task<CommandContinuation> RunAsync(string input, ConsoleChatSession session)
    {
        string folderPath = input.Length > "/add-folder".Length
            ? input["/add-folder".Length..].Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            folderPath = AnsiConsole.Ask<string>("Folder path:");
        }

        if (!Directory.Exists(folderPath))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {folderPath}");
            return CommandContinuation.Continue;
        }

        string defaultPattern = session.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value.SearchPattern;
        string pattern = AnsiConsole.Ask("File pattern", defaultPattern);

        IProjectSessionState state = session.ServiceProvider.GetRequiredService<IProjectSessionState>();
        int fileCount = IngestionCommandService.CountFiles(folderPath, pattern);

        if (fileCount == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No files match pattern {pattern} in {folderPath}[/]");
            return CommandContinuation.Continue;
        }

        if (fileCount > 50 && !AnsiConsole.Confirm($"This will ingest {fileCount} files. Continue?", defaultValue: false))
        {
            return CommandContinuation.Continue;
        }

        (string? sessionId, string? projectId) = ScopeResolutionHelper.Resolve(state);

        IngestionCommandService ingestor = session.ServiceProvider.GetRequiredService<IngestionCommandService>();

        IngestionResult result = new(Succeeded: 0, Failed: 0);
        await AnsiConsole.Status().StartAsync($"Ingesting {fileCount} file(s)...", async ctx =>
        {
            result = await ingestor.IngestDirectoryAsync(folderPath, pattern, state.UserId, sessionId, projectId);
            ctx.Status($"Done — {result.Succeeded} chunk(s) ingested.");
        });

        AnsiConsole.MarkupLine($"[green]Ingested {fileCount} file(s), {result.Succeeded} chunk(s).[/]");
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
