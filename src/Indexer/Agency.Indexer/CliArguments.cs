namespace Agency.Indexer;

/// <summary>A parsed command line: <c>agency-index &lt;command&gt; [--option value]... [--flag]...</c>.</summary>
/// <param name="Command">The command name.</param>
/// <param name="Options">Option values keyed by name without the leading dashes.</param>
/// <param name="Flags">Boolean flags that were present.</param>
internal sealed record CliArguments(string Command, IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Flags)
{
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal) { "wait", "help", "yes", "dry-run", "no-text" };

    /// <summary>Parses <paramref name="args"/>; throws <see cref="UsageException"/> on malformed input.</summary>
    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            return new CliArguments("help", new Dictionary<string, string>(), new HashSet<string>());
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        int i = 1;
        while (i < args.Count)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length == 2)
            {
                throw new UsageException($"Unexpected argument '{args[i]}'. Options take the form --name value.");
            }

            string name = args[i][2..];
            if (KnownFlags.Contains(name))
            {
                flags.Add(name);
                i += 1;
            }
            else if (i + 1 < args.Count)
            {
                options[name] = args[i + 1];
                i += 2;
            }
            else
            {
                throw new UsageException($"Option '--{name}' requires a value.");
            }
        }

        return new CliArguments(args[0], options, flags);
    }

    /// <summary>Returns the value of option <paramref name="name"/>, or <see langword="null"/> when absent.</summary>
    public string? Get(string name) => this.Options.GetValueOrDefault(name);

    /// <summary>Returns the value of option <paramref name="name"/>; throws <see cref="UsageException"/> when absent.</summary>
    public string Require(string name) => this.Get(name) ?? throw new UsageException($"Missing required option --{name}.");

    /// <summary>Returns option <paramref name="name"/> parsed as a number from 0 to 1, or <see langword="null"/> when absent.</summary>
    public double? GetFraction(string name)
    {
        string? raw = this.Get(name);
        if (raw is null)
        {
            return null;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && value is >= 0 and <= 1
            ? value
            : throw new UsageException($"Option --{name} must be a number between 0 and 1.");
    }

    /// <summary>Returns option <paramref name="name"/> parsed as a positive integer, or <paramref name="fallback"/> when absent.</summary>
    public int GetPositiveInt(string name, int fallback)
    {
        string? raw = this.Get(name);
        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, out int value) && value > 0
            ? value
            : throw new UsageException($"Option --{name} must be a positive integer.");
    }
}
