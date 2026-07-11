namespace Agency.Harness.Skills;

/// <summary>
/// Parses a <c>SKILL.md</c> file into a <see cref="Skill"/> record.
/// Supports shallow YAML frontmatter: scalars, booleans, and lists in three syntaxes
/// (space-separated, comma-separated, and YAML block lists). No third-party YAML library
/// is used — the frontmatter schema is intentionally narrow.
/// </summary>
internal static class SkillParser
{
    private const string FrontmatterDelimiter = "---";

    /// <summary>
    /// Parses <paramref name="text"/> (the full contents of a <c>SKILL.md</c> file) into a <see cref="Skill"/>.
    /// </summary>
    /// <param name="text">The raw file text.</param>
    /// <param name="skillDir">Absolute path of the directory that contains the <c>SKILL.md</c> file.</param>
    /// <param name="dirName">
    /// The directory name — used as the canonical <see cref="Skill.Name"/> (the invocation key).
    /// A frontmatter <c>name</c> field, if present, is ignored for resolution.
    /// </param>
    /// <returns>A fully populated <see cref="Skill"/>.</returns>
    public static Skill Parse(string text, string skillDir, string dirName)
    {
        (Dictionary<string, string> fields, List<string> listFields, string body) = SplitFrontmatter(text);

        string description = GetScalar(fields, "description")
            ?? FirstBodyParagraph(body)
            ?? string.Empty;

        string? whenToUse = GetScalar(fields, "when_to_use");

        bool disableModelInvocation = GetBool(fields, "disable-model-invocation", defaultValue: false);
        bool userInvocable = GetBool(fields, "user-invocable", defaultValue: true);

        IReadOnlyList<string> arguments = GetList(fields, listFields, "arguments");
        IReadOnlyList<string> allowedTools = GetList(fields, listFields, "allowed-tools");
        string? shell = GetScalar(fields, "shell");
        string? argumentHint = GetScalar(fields, "argument-hint");
        string? context = GetScalar(fields, "context");
        string? agent = GetScalar(fields, "agent");

        return new Skill
        {
            Name = dirName,
            Description = description,
            WhenToUse = whenToUse,
            Body = body,
            SkillDir = skillDir,
            DisableModelInvocation = disableModelInvocation,
            UserInvocable = userInvocable,
            Arguments = arguments,
            AllowedTools = allowedTools,
            Shell = shell,
            ArgumentHint = argumentHint,
            Context = context,
            Agent = agent,
        };
    }

    /// <summary>
    /// Splits the text into frontmatter scalar/list fields and the body.
    /// Returns empty collections when no frontmatter is present.
    /// </summary>
    private static (Dictionary<string, string> scalars, List<string> listKeys, string body) SplitFrontmatter(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        string[] lines = normalized.Split('\n');

        // Frontmatter must start with --- on the very first line.
        if (lines.Length < 2 || lines[0].Trim() != FrontmatterDelimiter)
        {
            return ([], [], text.TrimStart());
        }

        int closeIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == FrontmatterDelimiter)
            {
                closeIndex = i;
                break;
            }
        }

        if (closeIndex < 0)
        {
            // Opening --- found but no closing --- — treat whole text as body.
            return ([], [], text.TrimStart());
        }

        string[] frontmatterLines = lines[1..closeIndex];
        string body = string.Join("\n", lines[(closeIndex + 1)..]).TrimStart('\n');

        return ParseYamlLines(frontmatterLines, body);
    }

    /// <summary>
    /// Parses shallow YAML lines into scalar and list-valued fields.
    /// </summary>
    private static (Dictionary<string, string> scalars, List<string> listKeys, string body) ParseYamlLines(
        string[] yamlLines, string body)
    {
        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var listKeys = new List<string>();

        string? currentListKey = null;
        var currentListValues = new List<string>();

        void FlushList()
        {
            if (currentListKey is not null && currentListValues.Count > 0)
            {
                // Store block-list values as newline-joined so GetList can detect them.
                scalars[currentListKey] = string.Join("\n", currentListValues);
                listKeys.Add(currentListKey);
            }

            currentListKey = null;
            currentListValues.Clear();
        }

        foreach (string rawLine in yamlLines)
        {
            string line = rawLine.TrimEnd();

            // YAML block list item (  - value).
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) && currentListKey is not null)
            {
                currentListValues.Add(line.TrimStart()[2..].Trim());
                continue;
            }

            // Any non-list-item line closes the current block list.
            FlushList();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex < 0)
            {
                continue;
            }

            string key = line[..colonIndex].Trim();
            string value = line[(colonIndex + 1)..].Trim();

            if (string.IsNullOrEmpty(value))
            {
                // Start of a block list — subsequent "- item" lines belong to this key.
                currentListKey = key;
            }
            else
            {
                scalars[key] = value;
            }
        }

        FlushList();

        return (scalars, listKeys, body);
    }

    /// <summary>Returns the scalar value for <paramref name="key"/>, or <see langword="null"/> if absent.</summary>
    private static string? GetScalar(Dictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out string? value) ? value : null;
    }

    /// <summary>
    /// Returns the boolean value for <paramref name="key"/>, interpreting <c>true</c>/<c>false</c> (case-insensitive).
    /// Falls back to <paramref name="defaultValue"/> when the key is absent or unrecognised.
    /// </summary>
    private static bool GetBool(Dictionary<string, string> fields, string key, bool defaultValue)
    {
        if (!fields.TryGetValue(key, out string? raw))
        {
            return defaultValue;
        }

        string trimmed = raw.Trim();

        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }

    /// <summary>
    /// Returns the list value for <paramref name="key"/>, supporting three syntaxes:
    /// <list type="bullet">
    ///   <item>YAML block list (key stored in <paramref name="listKeys"/>; value is newline-joined items)</item>
    ///   <item>Comma-separated inline value: <c>a, b, c</c></item>
    ///   <item>Space-separated inline value: <c>a b c</c></item>
    /// </list>
    /// </summary>
    private static List<string> GetList(
        Dictionary<string, string> fields,
        List<string> listKeys,
        string key)
    {
        if (!fields.TryGetValue(key, out string? raw))
        {
            return [];
        }

        // Block list: values stored as newline-joined by ParseYamlLines.
        if (listKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return raw.Split('\n')
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToList();
        }

        // Comma-separated inline.
        if (raw.Contains(','))
        {
            return raw.Split(',')
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToList();
        }

        // Space-separated inline.
        return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.Length > 0)
                  .ToList();
    }

    /// <summary>
    /// Returns the first non-empty paragraph from the body, stripping leading Markdown heading markers.
    /// Returns <see langword="null"/> when the body is empty.
    /// </summary>
    private static string? FirstBodyParagraph(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string normalized = body.Replace("\r\n", "\n").Replace("\r", "\n");

        foreach (string paragraph in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = paragraph.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            // Strip leading Markdown heading markers (# ## ###…).
            if (trimmed.StartsWith('#'))
            {
                trimmed = trimmed.TrimStart('#').TrimStart();
            }

            if (!string.IsNullOrEmpty(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }
}
