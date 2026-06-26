using Agency.Harness;
using Agency.Harness.Console.Services;
using Microsoft.Extensions.DependencyInjection;
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

        string pattern = AnsiConsole.Ask("File pattern", "*.md");

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

        int totalChunks = 0;
        await AnsiConsole.Status().StartAsync($"Ingesting {fileCount} file(s)...", async ctx =>
        {
            totalChunks = await ingestor.IngestDirectoryAsync(folderPath, pattern, state.UserId, sessionId, projectId);
            ctx.Status($"Done — {totalChunks} chunk(s) ingested.");
        });

        AnsiConsole.MarkupLine($"[green]Ingested {fileCount} file(s), {totalChunks} chunk(s).[/]");

        DocumentContextHydrationService hydration =
            session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
        hydration.MarkDirty();

        return CommandContinuation.Continue;
    }
}
