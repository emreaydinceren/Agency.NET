using Spectre.Console;

namespace Agency.Harness.Console.Commands;

internal static class HelpCommand
{
    internal static CommandContinuation Run()
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Command");
        table.AddColumn("Description");

        foreach (Command command in CommandRegistry.Commands.OrderBy(c => c.CommandText, StringComparer.OrdinalIgnoreCase))
        {
            string commandColumn = command.ArgumentHint is null
                ? Markup.Escape(command.CommandText)
                : $"{Markup.Escape(command.CommandText)} [grey]{Markup.Escape(command.ArgumentHint)}[/]";

            table.AddRow(commandColumn, Markup.Escape(command.Description));
        }

        AnsiConsole.Write(table);
        return CommandContinuation.Continue;
    }
}
