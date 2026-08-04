using Agency.Memory.Common.Storage;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class MemoryCommand
{
    public static Task<CommandContinuation> Toggle(ConsoleChatSession session)
    {
        ChatSession? chatSession = session.CurrentSession;
        if (chatSession is null)
        {
            AnsiConsole.MarkupLine("[yellow]No active session.[/]");
            return Task.FromResult(CommandContinuation.Continue);
        }

        bool enabled = !chatSession.MemoryEnabled;
        chatSession.SetMemoryEnabled(enabled);

        AnsiConsole.MarkupLine(enabled
            ? "[green]Memory enabled for this session.[/]"
            : "[yellow]Memory disabled for this session.[/]");

        if (enabled && session.ServiceProvider.GetService<IMemoryStore>() is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Note: the memory subsystem was not started (Memory:Enabled=false at launch), " +
                "so this toggle has no effect until the app is restarted with it enabled.[/]");
        }

        return Task.FromResult(CommandContinuation.Continue);
    }
}
