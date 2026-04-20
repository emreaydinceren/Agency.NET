namespace Agency.Agentic.Console.Telemetry;

/// <summary>Root options bound from the <c>OpenTelemetry</c> configuration section.</summary>
internal sealed class TelemetryOptions
{
    /// <summary>OTel resource service name.</summary>
    public string ServiceName { get; set; } = "Agency.Agentic.Console";

    /// <summary>File export settings for all telemetry signals.</summary>
    public FileExportOptions FileExport { get; set; } = new();
}

/// <summary>Controls file output for all telemetry signals.</summary>
internal sealed class FileExportOptions
{
    /// <summary>Base directory where all log files are written. Created if absent.</summary>
    public string OutputDirectory { get; set; } = "./logs";

    /// <summary>Trace export settings.</summary>
    public TraceFileOptions Traces { get; set; } = new();

    /// <summary>Metric export settings.</summary>
    public MetricFileOptions Metrics { get; set; } = new();

    /// <summary>Structured log (Serilog) settings.</summary>
    public LogFileOptions Logs { get; set; } = new();
}

/// <summary>Trace-specific file export options.</summary>
internal sealed class TraceFileOptions
{
    /// <summary>When <see langword="false"/>, no <see cref="OpenTelemetry.Trace.TracerProvider"/> is built.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Date-stamped file name prefix, e.g. <c>traces-2026-04-15.log</c>.</summary>
    public string FilePrefix { get; set; } = "traces";

    /// <summary>Fraction of traces to sample (0.0–1.0). <c>1.0</c> = always on.</summary>
    public double SamplingRatio { get; set; } = 1.0;
}

/// <summary>Metrics-specific file export options.</summary>
internal sealed class MetricFileOptions
{
    /// <summary>When <see langword="false"/>, no <see cref="OpenTelemetry.Metrics.MeterProvider"/> is built.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Date-stamped file name prefix, e.g. <c>metrics-2026-04-15.log</c>.</summary>
    public string FilePrefix { get; set; } = "metrics";

    /// <summary>Milliseconds between metric export cycles.</summary>
    public int ExportIntervalMs { get; set; } = 15000;
}

/// <summary>Structured logging (Serilog) options.</summary>
internal sealed class LogFileOptions
{
    /// <summary>When <see langword="false"/>, only a null logger is registered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Date-stamped file name prefix, e.g. <c>app-2026-04-15.log</c>.</summary>
    public string FilePrefix { get; set; } = "app";

    /// <summary>
    /// Minimum log level. Valid values (case-insensitive):
    /// Verbose, Debug, Information, Warning, Error, Fatal.
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";
}
