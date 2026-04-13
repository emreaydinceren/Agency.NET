namespace Agency.Agentic.Console;

using System.Text;

/// <summary>
/// Renders an inline multi-column picker UI in the console with arrow-key navigation.
/// </summary>
internal static class ConsolePicker
{
    /// <summary>
    /// Renders an inline multi-column picker below the current input line. Arrow keys navigate; Enter confirms; Escape dismisses.
    /// Each item is a <see cref="string"/> array whose columns are padded to align on screen.
    /// </summary>
    /// <param name="items">Rows to display; each element is an array of column values.</param>
    /// <param name="returnItemIndex">Index of the column whose value is returned on selection.</param>
    /// <param name="inputTop">Cursor row of the active input line.</param>
    /// <param name="inputLeft">Cursor column right after the last typed character.</param>
    /// <param name="header">Optional header text displayed above the picker with dimmer styling; if <see langword="null"/>, not shown.</param>
    /// <param name="description">Optional description text displayed above the items with dimmer styling; if <see langword="null"/>, not shown.</param>
    /// <returns>
    /// The value at <paramref name="returnItemIndex"/> for the selected row, or <see langword="null"/> if dismissed.
    /// </returns>
    internal static string? Show(IEnumerable<string[]> items, int returnItemIndex, int inputTop, int inputLeft, string? header = null, string? description = null)
    {
        string[][] rows = items.ToArray();
        if (rows.Length == 0)
        {
            return null;
        }

        // Compute the maximum width for each column position.
        int colCount = rows.Max(r => r.Length);
        int[] colWidths = new int[colCount];
        foreach (string[] row in rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                colWidths[c] = Math.Max(colWidths[c], row[c].Length);
            }
        }

        // Calculate total width: 1 leading space + columns + 2 spaces between cols + 1 trailing space.
        int contentWidth = colWidths.Sum() + (colCount - 1) * 2;
        int totalWidth = 1 + contentWidth + 1;

        // Expand to fill console width if available.
        int consoleWidth = System.Console.WindowWidth - 2; // Account for left margin
        if (totalWidth < consoleWidth)
        {
            int extraSpace = consoleWidth - totalWidth;
            colWidths[colCount - 1] += extraSpace;
            totalWidth = consoleWidth;
        }

        // Calculate item offset based on header and description.
        int itemOffset = 0;
        if (!string.IsNullOrEmpty(header))
        {
            itemOffset++;
        }

        if (!string.IsNullOrEmpty(description))
        {
            itemOffset++;
        }

        int selectedIndex = 0;

        void Render()
        {
            // Render header with dimmer styling.
            if (!string.IsNullOrEmpty(header))
            {
                System.Console.SetCursorPosition(2, inputTop + 1);
                System.Console.Write(new string(' ', totalWidth));
                System.Console.SetCursorPosition(2, inputTop + 1);
                System.Console.ForegroundColor = ConsoleColor.Gray;
                System.Console.Write(header);
                System.Console.ResetColor();
            }

            // Render description with dimmer styling.
            if (!string.IsNullOrEmpty(description))
            {
                int descRow = inputTop + 1 + (string.IsNullOrEmpty(header) ? 0 : 1);
                System.Console.SetCursorPosition(2, descRow);
                System.Console.Write(new string(' ', totalWidth));
                System.Console.SetCursorPosition(2, descRow);
                System.Console.ForegroundColor = ConsoleColor.Gray;
                System.Console.Write(description);
                System.Console.ResetColor();
            }

            // Render items.
            for (int i = 0; i < rows.Length; i++)
            {
                System.Console.SetCursorPosition(2, inputTop + 1 + itemOffset + i);
                System.Console.Write(new string(' ', totalWidth));
                System.Console.SetCursorPosition(2, inputTop + 1 + itemOffset + i);

                if (i == selectedIndex)
                {
                    System.Console.BackgroundColor = ConsoleColor.DarkBlue;
                    System.Console.ForegroundColor = ConsoleColor.White;
                }

                var sb = new StringBuilder(" ");
                for (int c = 0; c < rows[i].Length; c++)
                {
                    sb.Append(rows[i][c].PadRight(colWidths[c]));
                    if (c < rows[i].Length - 1)
                    {
                        sb.Append("  ");
                    }
                }

                sb.Append(' ');
                System.Console.Write(sb.ToString());
                System.Console.ResetColor();
            }

            System.Console.SetCursorPosition(inputLeft, inputTop);
        }

        void Clear()
        {
            // Clear header if present.
            if (!string.IsNullOrEmpty(header))
            {
                System.Console.SetCursorPosition(0, inputTop + 1);
                System.Console.Write(new string(' ', totalWidth + 2));
            }

            // Clear description if present.
            if (!string.IsNullOrEmpty(description))
            {
                int descRow = inputTop + 1 + (string.IsNullOrEmpty(header) ? 0 : 1);
                System.Console.SetCursorPosition(0, descRow);
                System.Console.Write(new string(' ', totalWidth + 2));
            }

            // Clear items.
            for (int i = 0; i < rows.Length; i++)
            {
                System.Console.SetCursorPosition(0, inputTop + 1 + itemOffset + i);
                System.Console.Write(new string(' ', totalWidth + 2));
            }

            System.Console.SetCursorPosition(inputLeft, inputTop);
        }

        Render();

        while (true)
        {
            ConsoleKeyInfo key = System.Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selectedIndex > 0)
                    {
                        selectedIndex--;
                        Render();
                    }

                    break;

                case ConsoleKey.DownArrow:
                    if (selectedIndex < rows.Length - 1)
                    {
                        selectedIndex++;
                        Render();
                    }

                    break;

                case ConsoleKey.Enter:
                    Clear();
                    string[] selected = rows[selectedIndex];
                    return returnItemIndex < selected.Length ? selected[returnItemIndex] : null;

                case ConsoleKey.Escape:
                    Clear();
                    return null;
            }
        }
    }
}
