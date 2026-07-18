
using Spectre.Console;
using System.Text;

namespace Agency.Harness.Console;
internal class ConsolePickerRow(params string[] values)
{
    public string[] Values { get; } = values;

    public string this[int index] => Values[index];
}

/// <summary>
/// An item shown in a <see cref="ConsolePicker"/> list.
/// </summary>
/// <typeparam name="T">The type returned when this item is selected.</typeparam>
/// <param name="value">The value returned when this item is selected.</param>
/// <param name="displayText">The text rendered for this item.</param>
/// <param name="searchText">The text matched against the user's typed filter (startsWith, case-insensitive).</param>
/// <param name="groupLabel">Optional group header rendered above this item when it differs from the previous item's group.</param>
internal readonly struct ConsolePickerItem<T>(T value, string displayText, string searchText, string? groupLabel = null)
{
    public T Value { get; } = value;

    public string DisplayText { get; } = displayText;

    public string SearchText { get; } = searchText;

    public string? GroupLabel { get; } = groupLabel;
}

/// <summary>
/// Renders an inline picker UI in the console with arrow-key navigation and live
/// startsWith filtering as the user types.
/// </summary>
internal static class ConsolePicker
{
    public static string? Show(
    IReadOnlyCollection<ConsolePickerRow> rows,
    int returnItemIndex,
    string? title = null,
    string? moreChoicesText = null,
    int pageSize = 10)
    {
        Dictionary<int, int> maxWidthOfColumn = new Dictionary<int, int>();

        foreach(var row in rows)
        {
            for(int colIndex = 0; colIndex < row.Values.Length; colIndex++)
            {
                if (maxWidthOfColumn.TryGetValue(colIndex, out var maxWidth) == false)
                {
                    maxWidthOfColumn[colIndex] = row[colIndex].Length;
                }
                else
                {
                    maxWidthOfColumn[colIndex] = Math.Max(maxWidth, row[colIndex].Length);
                }
            }
        }

        List<ConsolePickerItem<string>> items = new(rows.Count);
        foreach (var row in rows)
        {
            var line = new StringBuilder();
            for (int i = 0; i < row.Values.Length; i++)
            {
                line.Append(row.Values[i].PadRight(maxWidthOfColumn[i] + 2));
            }

            items.Add(new ConsolePickerItem<string>(
                value: row[returnItemIndex],
                displayText: line.ToString().TrimEnd(),
                searchText: row[0].TrimStart('/')));
        }

        return Show(items, title, moreChoicesText, pageSize: pageSize);
    }

    public static string? Show(
    string[] options,
    string? title = null,
    string? moreChoicesText = null,
    int pageSize = 10)
    {
        var items = options.Select(o => new ConsolePickerItem<string>(o, o, o)).ToList();
        return Show(items, title, moreChoicesText, pageSize: pageSize);
    }

    /// <summary>
    /// Presents an inline, filterable list picker. Typed characters filter <paramref name="items"/>
    /// live via a case-insensitive startsWith match against each item's search text; arrow keys
    /// navigate, Enter selects, and Escape cancels (returning <paramref name="cancelValue"/>).
    /// </summary>
    /// <param name="items">The selectable items.</param>
    /// <param name="title">Optional title text shown above the picker; if <see langword="null"/>, not shown.</param>
    /// <param name="moreChoicesText">Hint text shown when more items exist than fit on one page.</param>
    /// <param name="filterPlaceholderText">Placeholder text shown while no filter text has been typed.</param>
    /// <param name="cancelValue">Value returned if the picker is cancelled via Escape.</param>
    /// <param name="pageSize">Number of items to show per page. Defaults to 10.</param>
    /// <returns>The selected item's value, or <paramref name="cancelValue"/> if cancelled.</returns>
    public static T? Show<T>(
        IReadOnlyList<ConsolePickerItem<T>> items,
        string? title = null,
        string? moreChoicesText = null,
        string? filterPlaceholderText = null,
        T? cancelValue = default,
        int pageSize = 10) where T : notnull
    {
        if (items.Count == 0)
        {
            return cancelValue;
        }

        // Anchor the block to column 0 so redraw/erase math below never has to
        // account for a partial line the caller left behind.
        if (System.Console.CursorLeft != 0)
        {
            System.Console.Write(Environment.NewLine);
        }

        var console = AnsiConsole.Console;
        var filter = new StringBuilder();
        int selectedIndex = 0;
        int lastRenderedLineCount = 0;

        AnsiConsole.Cursor.Hide();
        try
        {
            while (true)
            {
                List<ConsolePickerItem<T>> filtered = items
                    .Where(item => item.SearchText.StartsWith(filter.ToString(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (selectedIndex >= filtered.Count)
                {
                    selectedIndex = Math.Max(0, filtered.Count - 1);
                }

                List<string> lines = BuildRenderLines(
                    filtered, title, moreChoicesText, filterPlaceholderText, filter.ToString(), selectedIndex, pageSize);
                Redraw(lines, ref lastRenderedLineCount);

                ConsoleKeyInfo? keyInfo = console.Input.ReadKey(intercept: true);
                if (keyInfo is null)
                {
                    continue;
                }

                switch (keyInfo.Value.Key)
                {
                    case ConsoleKey.Escape:
                    Erase(lastRenderedLineCount);
                    return cancelValue;

                    case ConsoleKey.Enter:
                    if (filtered.Count > 0)
                    {
                        Erase(lastRenderedLineCount);
                        return filtered[selectedIndex].Value;
                    }

                    break;

                    case ConsoleKey.UpArrow:
                    if (filtered.Count > 0)
                    {
                        selectedIndex = (selectedIndex - 1 + filtered.Count) % filtered.Count;
                    }

                    break;

                    case ConsoleKey.DownArrow:
                    if (filtered.Count > 0)
                    {
                        selectedIndex = (selectedIndex + 1) % filtered.Count;
                    }

                    break;

                    case ConsoleKey.Backspace:
                    if (filter.Length > 0)
                    {
                        filter.Remove(filter.Length - 1, 1);
                        selectedIndex = 0;
                    }

                    break;

                    default:
                    if (!char.IsControl(keyInfo.Value.KeyChar))
                    {
                        // A space typed right after the filter text exactly spells out the sole
                        // remaining match's search text reads as "confirm this and start typing
                        // the next word" (e.g. an argument after a command name), not as more
                        // filter text — which would otherwise instantly break the match (nothing
                        // starts with "<name> ") and strand the user on a dead "(no matches)"
                        // screen with Enter doing nothing. Requiring an exact match (rather than
                        // just a single candidate) avoids misfiring mid-word on multi-word labels
                        // like "Allow once", where the internal space is itself part of the text.
                        if (keyInfo.Value.KeyChar == ' '
                            && filtered.Count == 1
                            && filtered[0].SearchText.Equals(filter.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            Erase(lastRenderedLineCount);
                            return filtered[selectedIndex].Value;
                        }

                        filter.Append(keyInfo.Value.KeyChar);
                        selectedIndex = 0;
                    }

                    break;
                }
            }
        }
        finally
        {
            AnsiConsole.Cursor.Show();
        }
    }

    private static List<string> BuildRenderLines<T>(
        List<ConsolePickerItem<T>> filtered,
        string? title,
        string? moreChoicesText,
        string? filterPlaceholderText,
        string filterText,
        int selectedIndex,
        int pageSize) where T : notnull
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(title))
        {
            lines.Add($"[bold]{Markup.Escape(title)}[/]");
        }

        string filterDisplay = filterText.Length > 0
            ? Markup.Escape(filterText)
            : $"[grey]{Markup.Escape(filterPlaceholderText ?? "Type to filter")}[/]";
        lines.Add($"[grey]›[/] {filterDisplay}");

        if (filtered.Count == 0)
        {
            lines.Add("[grey](no matches)[/]");
            return lines;
        }

        int windowStart = 0;
        if (filtered.Count > pageSize)
        {
            windowStart = Math.Clamp(selectedIndex - (pageSize / 2), 0, filtered.Count - pageSize);
        }

        int windowEnd = Math.Min(filtered.Count, windowStart + pageSize);

        string? currentGroup = null;
        for (int i = windowStart; i < windowEnd; i++)
        {
            ConsolePickerItem<T> item = filtered[i];
            if (item.GroupLabel is not null && item.GroupLabel != currentGroup)
            {
                currentGroup = item.GroupLabel;
                lines.Add($"[grey]{Markup.Escape(currentGroup)}[/]");
            }

            string text = Markup.Escape(item.DisplayText);
            lines.Add(i == selectedIndex ? $"[yellow]❯ {text}[/]" : $"  {text}");
        }

        if (filtered.Count > pageSize)
        {
            lines.Add($"[grey]{Markup.Escape(moreChoicesText ?? "(Move up and down to reveal more choices)")}[/]");
        }

        return lines;
    }

    private static void Redraw(List<string> lines, ref int lastRenderedLineCount)
    {
        Erase(lastRenderedLineCount);
        foreach (string line in lines)
        {
            AnsiConsole.Console.MarkupLine(line);
        }

        lastRenderedLineCount = lines.Count;
    }

    private static void Erase(int lastRenderedLineCount)
    {
        System.Console.Write('\r');
        if (lastRenderedLineCount > 0)
        {
            AnsiConsole.Cursor.MoveUp(lastRenderedLineCount);
        }

        // Erase from cursor to end of display — robust against terminal scrolling,
        // unlike absolute Console.SetCursorPosition-based clearing.
        System.Console.Write("\u001b[0J");
    }
}
