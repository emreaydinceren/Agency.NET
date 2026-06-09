namespace Agency.Utils.HttpCacheProxy.Telemetry;

internal sealed class TelemetryOptions
{
    public string ServiceName { get; set; } = "Agency.Utils.HttpCacheProxy";
    public FileExportOptions FileExport { get; set; } = new();
}

internal sealed class FileExportOptions
{
    public string OutputDirectory { get; set; } = "./logs";
    public TraceFileOptions Traces { get; set; } = new();
    public MetricFileOptions Metrics { get; set; } = new();
    public LogFileOptions Logs { get; set; } = new();
}

internal sealed class TraceFileOptions
{
    public bool Enabled { get; set; } = true;
    public string FilePrefix { get; set; } = "traces";
    public double SamplingRatio { get; set; } = 1.0;
}

internal sealed class MetricFileOptions
{
    public bool Enabled { get; set; } = true;
    public string FilePrefix { get; set; } = "metrics";
    public int ExportIntervalMs { get; set; } = 15000;
}

internal sealed class LogFileOptions
{
    public bool Enabled { get; set; } = true;
    public string FilePrefix { get; set; } = "app";
    public string MinimumLevel { get; set; } = "Information";
}
