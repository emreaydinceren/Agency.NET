namespace Agency.Indexer;

/// <summary>
/// Where an <c>index</c> run reports progress and failures: every line goes to stderr (stdout stays one JSON object for
/// the calling agent) and, with <c>--log &lt;file&gt;</c>, is appended to that file with a UTC timestamp.
/// </summary>
internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly Action<string> _echo;

    /// <summary>Opens <paramref name="path"/> for appending, or logs to stderr only when it is <see langword="null"/>.</summary>
    /// <param name="path">The log file, created with its folders if needed.</param>
    /// <param name="echo">Receives each line; defaults to stderr.</param>
    public RunLog(string? path, Action<string>? echo = null)
    {
        this._echo = echo ?? Console.Error.WriteLine;
        if (path is not null)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            this._file = new StreamWriter(new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
    }

    /// <summary>Reports one line.</summary>
    public void Write(string line)
    {
        this._echo(line);
        this._file?.WriteLine($"{DateTimeOffset.UtcNow:O} {line}");
    }

    /// <inheritdoc/>
    public void Dispose() => this._file?.Dispose();
}
