namespace Agency.Agentic.Console.Telemetry;

using System.Text;
using OpenTelemetry;
using OpenTelemetry.Metrics;

/// <summary>
/// Exports metric snapshots to a daily-rolling text file.
/// Each export cycle appends a timestamped block listing every metric with its current value and tags.
/// </summary>
internal sealed class FileMetricExporter : BaseExporter<Metric>
{
    private readonly string _outputDir;
    private readonly string _filePrefix;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";

    /// <summary>Initialises the exporter from resolved <see cref="FileExportOptions"/>.</summary>
    internal FileMetricExporter(FileExportOptions options)
    {
        this._outputDir = options.OutputDirectory;
        this._filePrefix = options.Metrics.FilePrefix;
        Directory.CreateDirectory(this._outputDir);
        this.EnsureWriter();
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<Metric> batch)
    {
        lock (this._lock)
        {
            try
            {
                this.EnsureWriter();
                this._writer?.WriteLine($"--- Metrics {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC ---");

                foreach (Metric metric in batch)
                {
                    this._writer?.Write($"  {metric.Name} [{metric.Unit ?? "none"}] {metric.MetricType}:");

                    foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                    {
                        switch (metric.MetricType)
                        {
                            case MetricType.LongSum:
                            case MetricType.LongSumNonMonotonic:
                                this._writer?.Write($" {point.GetSumLong()}");
                                break;
                            case MetricType.DoubleSum:
                            case MetricType.DoubleSumNonMonotonic:
                                this._writer?.Write($" {point.GetSumDouble():F2}");
                                break;
                            case MetricType.LongGauge:
                                this._writer?.Write($" {point.GetGaugeLastValueLong()}");
                                break;
                            case MetricType.DoubleGauge:
                                this._writer?.Write($" {point.GetGaugeLastValueDouble():F2}");
                                break;
                            case MetricType.Histogram:
                                this._writer?.Write($" count={point.GetHistogramCount()} sum={point.GetHistogramSum():F2}");
                                break;
                        }

                        foreach (KeyValuePair<string, object?> tag in point.Tags)
                        {
                            this._writer?.Write($" {tag.Key}={tag.Value}");
                        }
                    }

                    this._writer?.WriteLine();
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
