
using System.Diagnostics;
using System.Text;
using OpenTelemetry;

namespace Agency.Harness.Console.Telemetry;
/// <summary>
/// Exports completed <see cref="Activity"/> spans to a daily-rolling text file.
/// Each span is written as one line containing trace identifiers, timing, status, and tags.
/// </summary>
internal sealed class FileSpanExporter : BaseExporter<Activity>
{
    private readonly DailyRollingFileWriter _fileWriter;

    /// <summary>Initialises the exporter from resolved <see cref="FileExportOptions"/>.</summary>
    internal FileSpanExporter(FileExportOptions options)
    {
        this._fileWriter = new DailyRollingFileWriter(options.OutputDirectory, options.Traces.FilePrefix);
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<Activity> batch)
    {
        try
        {
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

                this._fileWriter.WriteLine(sb.ToString());
            }

            return this._fileWriter.Flush() ? ExportResult.Success : ExportResult.Failure;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileSpanExporter] Export failed: {ex}");
            return ExportResult.Failure;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this._fileWriter.Dispose();
        }

        base.Dispose(disposing);
    }
}
