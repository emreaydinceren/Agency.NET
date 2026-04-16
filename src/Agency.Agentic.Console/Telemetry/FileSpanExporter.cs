namespace Agency.Agentic.Console.Telemetry;

using System.Diagnostics;
using System.Text;
using OpenTelemetry;

/// <summary>
/// Exports completed <see cref="Activity"/> spans to a daily-rolling text file.
/// Each span is written as one line containing trace identifiers, timing, status, and tags.
/// </summary>
internal sealed class FileSpanExporter : BaseExporter<Activity>
{
    private readonly string _outputDir;
    private readonly string _filePrefix;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";

    /// <summary>Initialises the exporter from resolved <see cref="FileExportOptions"/>.</summary>
    internal FileSpanExporter(FileExportOptions options)
    {
        this._outputDir = options.OutputDirectory;
        this._filePrefix = options.Traces.FilePrefix;
        Directory.CreateDirectory(this._outputDir);
        this.EnsureWriter();
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<Activity> batch)
    {
        lock (this._lock)
        {
            try
            {
                this.EnsureWriter();

                foreach (Activity activity in batch)
                {
                    StringBuilder sb = new();
                    sb.Append($"[{activity.StartTimeUtc:yyyy-MM-dd HH:mm:ss.fff}] ");
                    sb.Append($"TraceId={activity.TraceId} ");
                    sb.Append($"SpanId={activity.SpanId} ");
                    sb.Append($"Parent={activity.ParentSpanId} ");
                    sb.Append($"Name={activity.DisplayName} ");
                    sb.Append($"Kind={activity.Kind} ");
                    sb.Append($"Status={activity.Status} ");
                    sb.Append($"Duration={activity.Duration.TotalMilliseconds:F1}ms");

                    foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
                    {
                        sb.Append($" {tag.Key}={tag.Value}");
                    }

                    foreach (ActivityEvent evt in activity.Events)
                    {
                        sb.Append($" | Event:{evt.Name}@{evt.Timestamp:HH:mm:ss.fff}");
                    }

                    this._writer?.WriteLine(sb.ToString());
                }

                this._writer?.Flush();
                return ExportResult.Success;
            }
            catch (Exception)
            {
                return ExportResult.Failure;
            }
        }
    }

    /// <summary>
    /// Opens or rolls the underlying <see cref="StreamWriter"/> when the UTC date changes.
    /// Uses <see cref="FileShare.Read"/> so the file can be read externally while the app runs.
    /// </summary>
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
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (this._lock)
            {
                this._writer?.Flush();
                this._writer?.Dispose();
                this._writer = null;
            }
        }

        base.Dispose(disposing);
    }
}
