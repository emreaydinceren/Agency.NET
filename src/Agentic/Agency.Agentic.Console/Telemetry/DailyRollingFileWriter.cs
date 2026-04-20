namespace Agency.Agentic.Console.Telemetry;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Thread-safe writer that appends lines to a date-stamped log file and rolls to a new file at midnight (UTC).
/// </summary>
internal sealed class DailyRollingFileWriter : IDisposable
{
    private readonly string _outputDir;
    private readonly string _filePrefix;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";

    internal DailyRollingFileWriter(string outputDir, string filePrefix)
    {
        this._outputDir = outputDir;
        this._filePrefix = filePrefix;
        Directory.CreateDirectory(outputDir);
        this.EnsureWriter();
    }

    /// <summary>
    /// Appends <paramref name="line"/> followed by a newline.
    /// Returns <c>false</c> if the write fails; the exception is traced to <see cref="Trace"/>.
    /// </summary>
    internal bool WriteLine(string line)
    {
        lock (this._lock)
        {
            try
            {
                this.EnsureWriter();
                this._writer?.WriteLine(line);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DailyRollingFileWriter] WriteLine failed for prefix '{this._filePrefix}': {ex}");
                return false;
            }
        }
    }

    /// <summary>Flushes the underlying stream. Returns <c>false</c> on failure.</summary>
    internal bool Flush()
    {
        lock (this._lock)
        {
            try
            {
                this._writer?.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DailyRollingFileWriter] Flush failed for prefix '{this._filePrefix}': {ex}");
                return false;
            }
        }
    }

    /// <summary>Opens or rolls the <see cref="StreamWriter"/> when the UTC date changes.</summary>
    private void EnsureWriter()
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (today == this._currentDate && this._writer != null)
        {
            return;
        }

        this._writer?.Flush();
        this._writer?.Dispose();

        this._currentDate = today;
        string filePath = Path.Combine(this._outputDir, $"{this._filePrefix}-{this._currentDate}.log");
        FileStream fileStream = new(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        this._writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = false };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this._lock)
        {
            try
            {
                this._writer?.Flush();
                this._writer?.Dispose();
                this._writer = null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DailyRollingFileWriter] Dispose failed for prefix '{this._filePrefix}': {ex}");
            }
        }
    }
}
