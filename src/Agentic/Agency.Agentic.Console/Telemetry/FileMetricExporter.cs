
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Agency.Agentic.Console.Telemetry;
/// <summary>
/// Exports metric snapshots to a daily-rolling text file.
/// Each export cycle appends a timestamped block listing every metric with its current value and tags.
/// </summary>
internal sealed class FileMetricExporter : BaseExporter<Metric>
{
    private readonly DailyRollingFileWriter _fileWriter;

    /// <summary>Initialises the exporter from resolved <see cref="FileExportOptions"/>.</summary>
    internal FileMetricExporter(FileExportOptions options)
    {
        this._fileWriter = new DailyRollingFileWriter(options.OutputDirectory, options.Metrics.FilePrefix);
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<Metric> batch)
    {
        try
        {
            this._fileWriter.WriteLine($"--- Metrics {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC ---");

            foreach (Metric metric in batch)
            {
                System.Text.StringBuilder sb = new();
                sb.Append($"  {metric.Name} [{metric.Unit ?? "none"}] {metric.MetricType}:");

                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    switch (metric.MetricType)
                    {
                        case MetricType.LongSum:
                        case MetricType.LongSumNonMonotonic:
                            sb.Append($" {point.GetSumLong()}");
                            break;
                        case MetricType.DoubleSum:
                        case MetricType.DoubleSumNonMonotonic:
                            sb.Append($" {point.GetSumDouble():F2}");
                            break;
                        case MetricType.LongGauge:
                            sb.Append($" {point.GetGaugeLastValueLong()}");
                            break;
                        case MetricType.DoubleGauge:
                            sb.Append($" {point.GetGaugeLastValueDouble():F2}");
                            break;
                        case MetricType.Histogram:
                            sb.Append($" count={point.GetHistogramCount()} sum={point.GetHistogramSum():F2}");
                            break;
                    }

                    foreach (KeyValuePair<string, object?> tag in point.Tags)
                    {
                        sb.Append($" {tag.Key}={tag.Value}");
                    }
                }

                this._fileWriter.WriteLine(sb.ToString());
            }

            return this._fileWriter.Flush() ? ExportResult.Success : ExportResult.Failure;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FileMetricExporter] Export failed: {ex}");
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
