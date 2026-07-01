
using Spectre.Console;
using System.Text;

namespace Agency.Harness.Console;
internal class ConsolePickerRow(params string[] values)
{
    public string[] Values { get; } = values;

    public string this[int index] => Values[index];
}

/// <summary>
/// Renders an inline multi-column picker UI in the console with arrow-key navigation.
/// </summary>
internal static class ConsolePicker
{
    public static string? Show(
    IReadOnlyCollection<ConsolePickerRow> rows,
    int returnItemIndex,
    string? title = null,
    string? moreChoicesText = null,
    bool? searchEnabled = false,
    string? searchPlaceholderText = null,
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

        string[] options = new string[rows.Count];
        Dictionary<string, string> reverseLookUp = new();

        int optionIndex = 0;
        foreach (var row in rows)
        {
            var line = new StringBuilder();
            for (int i = 0; i < row.Values.Length; i++)
            {
                string value = row.Values[i];
                line.Append(value.PadRight(maxWidthOfColumn[i] + 2));
            }

            options[optionIndex++] = line.ToString();
            reverseLookUp[line.ToString()] = row.Values[returnItemIndex];
        }

        var selected = Show(
        options, title,moreChoicesText, searchEnabled, searchPlaceholderText, pageSize);

        if (selected != null && reverseLookUp.TryGetValue(selected, out var original))
        {
            return original.Trim();
        }

        return null;
    }

    public static string? Show(
    string[] options,
    string? title = null,
    string? moreChoicesText = null,
    bool? searchEnabled = false,
    string? searchPlaceholderText = null,
    int pageSize = 10)
    {
        return Show<string>(prompt => 
        prompt.AddChoices(options),
        title, moreChoicesText, searchEnabled, searchPlaceholderText, null, null, pageSize);
    }

    /// <summary>
    /// Presents a single list prompt.
    /// </summary>
    /// <param name="promptFunc">
    /// A function that configures the <see cref="SelectionPrompt{T}"/> with items and styling.
    /// </param>
    /// <param name="title">Optional title text for the prompt; if <see langword="null"/>, not shown.</param>
    /// <param name="moreChoicesText">
    /// Optional hint text shown when more choices exist than fit on one page.
    /// <example>
    /// var selection = await ShowSelectionPromptAsync( prompt => prompt .AddChoiceGroup("Berries", new[] {
    /// "Blackcurrant", "Blueberry", "Cloudberry", "Elderberry", "Honeyberry", "Mulberry" }) .AddChoices(new[] {
    /// "Apple", "Apricot", "Avocado", "Banana", "Cherry", "Cocunut", "Date", "Dragonfruit", "Durian", "Egg plant",
    /// "Fig", "Grape", "Guava", "Jackfruit", "Jambul", "Kiwano", "Kiwifruit", "Lime", "Lylo", "Lychee", "Melon",
    /// "Nectarine", "Orange", "Olive" }), title: "Select your favorite [green]fruit[/]:", moreChoicesText: "[grey](Move
    /// up and down to reveal more fruits)[/]" );
    /// </example>
    /// </param>
    /// <param name="searchEnabled">When <see langword="true"/>, the user can type to filter the visible choices.</param>
    /// <param name="searchPlaceholderText">Optional placeholder text shown in the search box when search is enabled.</param>
    /// <param name="cancelValue">
    /// Value returned if the prompt is cancelled (e.g. via Escape); when left <see langword="default"/>, no
    /// cancel result is registered and cancellation instead falls through to the <see langword="default"/> return.
    /// </param>
    /// <param name="itemToStringConverter">Optional function used to render each item as display text.</param>
    /// <param name="pageSize">Number of items to show per page. Defaults to 10.</param>
    /// <returns>The selected item, or <paramref name="cancelValue"/> if the prompt was cancelled.</returns>
    public static T? Show<T>(
        Func<SelectionPrompt<T>, SelectionPrompt<T>> promptFunc,
        string? title = null,
        string? moreChoicesText = null,
        bool? searchEnabled = false,
        string? searchPlaceholderText = null,
        T? cancelValue = default,
        Func<T, string>? itemToStringConverter = null,
        int pageSize = 10) where T : notnull
    {
        var prompt = new SelectionPrompt<T> ().PageSize(pageSize);

        if (!string.IsNullOrWhiteSpace(title))
        {
            prompt = prompt.Title(title);
        }

        if (!string.IsNullOrWhiteSpace(moreChoicesText))
        {
            prompt = prompt.MoreChoicesText(moreChoicesText);
        }

        prompt = promptFunc(prompt);
        if (itemToStringConverter != null)
        {
            prompt.Converter = itemToStringConverter;
        }
        prompt.SearchEnabled = searchEnabled ?? false;
        prompt.SearchPlaceholderText = searchPlaceholderText;

        int startTop = System.Console.CursorTop;
        int startLeft = System.Console.CursorLeft;

        if (cancelValue != null)
        {
            prompt.AddCancelResult(cancelValue);
        }

        try
        {
            return AnsiConsole.Prompt(prompt);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        finally
        {
            int endTop = System.Console.CursorTop;
            for (int row = endTop; row >= startTop; row--)
            {
                System.Console.SetCursorPosition(0, row);
                System.Console.Write(new string(' ', System.Console.BufferWidth));
            }

            System.Console.SetCursorPosition(startLeft, startTop);
        }
    }
}
