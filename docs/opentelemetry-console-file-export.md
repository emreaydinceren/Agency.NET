# OpenTelemetry in a .NET Console App — Logging Everything to Local Files

A complete, working example of a .NET console application using the Generic Host that exports **traces, metrics, and logs** to local files via `appsettings.json`-driven configuration.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Project Setup](#project-setup)
3. [appsettings.json — Full Configuration](#appsettingsjson--full-configuration)
4. [Program.cs — Host & OpenTelemetry Wiring](#programcs--host--opentelemetry-wiring)
5. [TelemetryFileExporter.cs — Custom File Exporter](#telemetryfileexportercs--custom-file-exporter)
6. [Worker.cs — Sample Instrumented Service](#workercs--sample-instrumented-service)
7. [Output File Structure](#output-file-structure)
8. [Sample Output](#sample-output)
9. [Configuration Deep Dive](#configuration-deep-dive)
10. [Alternative: Serilog File Sink for Logs](#alternative-serilog-file-sink-for-logs)
11. [Tips & Gotchas](#tips--gotchas)

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────┐
│                 .NET Console App (Generic Host)       │
│                                                      │
│  ┌──────────┐   ┌──────────┐   ┌──────────────────┐ │
│  │  Traces   │   │ Metrics  │   │      Logs        │ │
│  │ (Activity │   │ (Meter)  │   │ (ILogger<T>)     │ │
│  │  Source)  │   │          │   │                  │ │
│  └────┬─────┘   └────┬─────┘   └───────┬──────────┘ │
│       │              │                  │            │
│       ▼              ▼                  ▼            │
│  Console Exporter  Console Exporter  File Logger     │
│  → StreamWriter    → StreamWriter    Provider        │
│       │              │                  │            │
│       ▼              ▼                  ▼            │
│  ./logs/traces.log  ./logs/metrics.log ./logs/app.log│
└──────────────────────────────────────────────────────┘
```

**Strategy:**

| Signal  | Exporter                       | File Target              |
|---------|--------------------------------|--------------------------|
| Traces  | `ConsoleExporter` → file writer | `./logs/traces-{date}.log` |
| Metrics | `ConsoleExporter` → file writer | `./logs/metrics-{date}.log`|
| Logs    | .NET `AddSimpleConsole` + redirect **or** custom file provider | `./logs/app-{date}.log` |

The `OpenTelemetry.Exporter.Console` accepts a custom `TextWriter`, which we point at rolling log files. For structured logs, we use .NET's built-in file logging or Serilog.

---

## Project Setup

### Create the project

```bash
dotnet new worker -n OtelFileDemo
cd OtelFileDemo
```

### Install packages

```bash
# OpenTelemetry core + hosting
dotnet add package OpenTelemetry.Extensions.Hosting

# Console exporter (we redirect its output to files)
dotnet add package OpenTelemetry.Exporter.Console

# Instrumentation
dotnet add package OpenTelemetry.Instrumentation.Http
dotnet add package OpenTelemetry.Instrumentation.Runtime

# File-based structured logging (pick one)
dotnet add package Serilog.Extensions.Hosting
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Formatting.Compact
# OR use the simpler built-in approach shown below
```

### .csproj additions

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.10.0" />
    <PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.10.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.10.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.10.0" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageReference Include="Serilog.Formatting.Compact" Version="3.0.0" />
  </ItemGroup>
</Project>
```

---

## appsettings.json — Full Configuration

This is the heart of the setup. Everything is driven from this file.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information",
      "OpenTelemetry": "Warning",
      "System.Net.Http.HttpClient": "Warning"
    },
    "OpenTelemetry": {
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true
    }
  },

  "OpenTelemetry": {
    "ServiceName": "otel-file-demo",
    "ServiceVersion": "1.0.0",
    "Environment": "development",

    "FileExport": {
      "Enabled": true,
      "OutputDirectory": "./logs",
      "RollingInterval": "Day",
      "RetainedFileCountLimit": 30,
      "FileSizeLimitBytes": 104857600,

      "Traces": {
        "Enabled": true,
        "FilePrefix": "traces",
        "FlushIntervalSeconds": 5
      },
      "Metrics": {
        "Enabled": true,
        "FilePrefix": "metrics",
        "FlushIntervalSeconds": 15
      },
      "Logs": {
        "Enabled": true,
        "FilePrefix": "app",
        "FlushIntervalSeconds": 2,
        "OutputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj} {Properties}{NewLine}{Exception}"
      }
    }
  },

  "OTEL_SERVICE_NAME": "otel-file-demo",
  "OTEL_RESOURCE_ATTRIBUTES": "deployment.environment=development,service.version=1.0.0",

  "OTEL_TRACES_SAMPLER": "always_on",

  "OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_ATTRIBUTE_COUNT_LIMIT": "128",

  "OTEL_SPAN_EVENT_COUNT_LIMIT": "128",
  "OTEL_SPAN_LINK_COUNT_LIMIT": "128",

  "OTEL_BSP_SCHEDULE_DELAY": "5000",
  "OTEL_BSP_MAX_QUEUE_SIZE": "2048",
  "OTEL_BSP_MAX_EXPORT_BATCH_SIZE": "512",

  "OTEL_BLRP_SCHEDULE_DELAY": "2000",
  "OTEL_BLRP_MAX_QUEUE_SIZE": "2048",
  "OTEL_BLRP_MAX_EXPORT_BATCH_SIZE": "512",

  "OTEL_METRIC_EXPORT_INTERVAL": "15000",
  "OTEL_METRIC_EXPORT_TIMEOUT": "10000",

  "Serilog": {
    "Using": ["Serilog.Sinks.File", "Serilog.Formatting.Compact"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning",
        "OpenTelemetry": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "./logs/app-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30,
          "fileSizeLimitBytes": 104857600,
          "rollOnFileSizeLimit": true,
          "outputTemplate": "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj} {Properties}{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
          "path": "./logs/app-structured-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30
        }
      }
    ],
    "Enrich": ["FromLogContext"]
  }
}
```

### What each section does

| Section | Purpose |
|---|---|
| `Logging` | .NET's built-in log level filtering + OpenTelemetry log provider options |
| `OpenTelemetry` | Custom section for service identity and file export settings |
| `OpenTelemetry:FileExport` | Controls output directory, rolling policy, and per-signal file prefixes |
| `OTEL_*` (flat keys) | Standard OTel SDK configuration read automatically by the SDK |
| `Serilog` | Serilog's file sink configuration — rolling daily logs with size limits |

---

## Program.cs — Host & OpenTelemetry Wiring

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace OtelFileDemo;

public sealed class OpenTelemetryOptions
{
    public string ServiceName { get; set; } = "unknown-service";
    public string ServiceVersion { get; set; } = "0.0.0";
    public string Environment { get; set; } = "development";
    public FileExportOptions FileExport { get; set; } = new();
}

public sealed class FileExportOptions
{
    public string OutputDirectory { get; set; } = "./logs";
    public SignalFileExportOptions Traces { get; set; } = new() { Enabled = true, FilePrefix = "traces" };
    public SignalFileExportOptions Metrics { get; set; } = new() { Enabled = true, FilePrefix = "metrics" };
    public SignalFileExportOptions Logs { get; set; } = new() { Enabled = true, FilePrefix = "app" };
}

public sealed class SignalFileExportOptions
{
    public bool Enabled { get; set; } = true;
    public string FilePrefix { get; set; } = "telemetry";
    public int FlushIntervalSeconds { get; set; } = 5;
}

public sealed class OpenTelemetryLoggingOptions
{
    public bool IncludeFormattedMessage { get; set; } = true;
    public bool IncludeScopes { get; set; } = true;
    public bool ParseStateValues { get; set; } = true;
}

public class Program
{
    // Define your ActivitySource and Meter at the application level
    public static readonly ActivitySource AppActivitySource = new("OtelFileDemo");
    public static readonly Meter AppMeter = new("OtelFileDemo", "1.0.0");

    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // ──────────────────────────────────────────────
        // 1. Bind strongly-typed options
        // ──────────────────────────────────────────────
        builder.Services
            .AddOptions<OpenTelemetryOptions>()
            .Bind(builder.Configuration.GetRequiredSection("OpenTelemetry"))
            .ValidateOnStart();

        builder.Services
            .AddOptions<OpenTelemetryLoggingOptions>()
            .Bind(builder.Configuration.GetRequiredSection("Logging:OpenTelemetry"))
            .ValidateOnStart();

        // Program.cs is startup glue, so materialize the same options contract once
        // for bootstrapping before the container is built.
        var otelOptions = builder.Configuration
            .GetRequiredSection("OpenTelemetry")
            .Get<OpenTelemetryOptions>()
            ?? throw new InvalidOperationException(
                "The OpenTelemetry configuration section is required.");

        var loggingOptions = builder.Configuration
            .GetSection("Logging:OpenTelemetry")
            .Get<OpenTelemetryLoggingOptions>()
            ?? new OpenTelemetryLoggingOptions();

        var outputDir = otelOptions.FileExport.OutputDirectory;

        // Ensure the output directory exists
        Directory.CreateDirectory(outputDir);

        // ──────────────────────────────────────────────
        // 2. Create file-backed TextWriters for exporters
        // ──────────────────────────────────────────────
        var tracesEnabled = otelOptions.FileExport.Traces.Enabled;
        var metricsEnabled = otelOptions.FileExport.Metrics.Enabled;

        var traceFilePrefix = otelOptions.FileExport.Traces.FilePrefix;
        var metricFilePrefix = otelOptions.FileExport.Metrics.FilePrefix;

        var traceFilePath = Path.Combine(outputDir,
            $"{traceFilePrefix}-{DateTime.UtcNow:yyyy-MM-dd}.log");
        var metricFilePath = Path.Combine(outputDir,
            $"{metricFilePrefix}-{DateTime.UtcNow:yyyy-MM-dd}.log");

        StreamWriter? traceWriter = null;
        StreamWriter? metricWriter = null;

        if (tracesEnabled)
        {
            traceWriter = new StreamWriter(traceFilePath, append: true)
            {
                AutoFlush = false
            };
        }

        if (metricsEnabled)
        {
            metricWriter = new StreamWriter(metricFilePath, append: true)
            {
                AutoFlush = false
            };
        }

        // ──────────────────────────────────────────────
        // 3. Configure OpenTelemetry
        // ──────────────────────────────────────────────
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: otelOptions.ServiceName,
                    serviceVersion: otelOptions.ServiceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = otelOptions.Environment,
                    ["host.name"] = System.Environment.MachineName
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AppActivitySource.Name)
                    .AddHttpClientInstrumentation();

                if (tracesEnabled && traceWriter != null)
                {
                    tracing.AddConsoleExporter(options =>
                    {
                        options.Targets = OpenTelemetry.Exporter.ConsoleExporterOutputTargets.Writer;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AppMeter.Name)
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (metricsEnabled && metricWriter != null)
                {
                    metrics.AddConsoleExporter();
                }
            });

        // ──────────────────────────────────────────────
        // 4. Configure Logging → Serilog (file) + OTel
        // ──────────────────────────────────────────────
        builder.Host.UseSerilog((context, loggerConfig) =>
        {
            loggerConfig.ReadFrom.Configuration(context.Configuration);
        });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = loggingOptions.IncludeFormattedMessage;
            logging.IncludeScopes = loggingOptions.IncludeScopes;
            logging.ParseStateValues = loggingOptions.ParseStateValues;

            logging.AddConsoleExporter();
        });

        // ──────────────────────────────────────────────
        // 5. Register the worker service
        // ──────────────────────────────────────────────
        builder.Services.AddHostedService<Worker>();

        // ──────────────────────────────────────────────
        // 6. Build & run, with cleanup
        // ──────────────────────────────────────────────
        var host = builder.Build();

        try
        {
            host.Run();
        }
        finally
        {
            traceWriter?.Flush();
            traceWriter?.Dispose();
            metricWriter?.Flush();
            metricWriter?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
```

This keeps the configuration contract in one place. After binding, downstream services can consume `IOptions<OpenTelemetryOptions>` or `IOptionsMonitor<OpenTelemetryOptions>` rather than reading raw configuration sections again.

### Simpler alternative — Redirect Console.Out to a file

If you don't need separate files per signal, the simplest approach replaces the above StreamWriter plumbing with a global redirect:

```csharp
// In Program.cs, before building the host:
var logFile = new StreamWriter("./logs/all-telemetry.log", append: true) { AutoFlush = true };
Console.SetOut(logFile);

// All Console exporters now write to this file automatically.
```

> **Caveat:** This captures *everything* that writes to `Console.Out`, including your own `Console.WriteLine` calls.

---

## TelemetryFileExporter.cs — Custom File Exporter

For production use with proper rolling, flushing, and per-signal separation, build a thin custom exporter. This gives you full control and clean `appsettings.json` integration.

```csharp
using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace OtelFileDemo;

/// <summary>
/// A simple file-based span exporter that writes one span per line
/// in a structured text format. Configured via appsettings.json.
/// </summary>
public sealed class FileSpanExporter : BaseExporter<Activity>
{
    private readonly string _outputDir;
    private readonly string _filePrefix;
    private readonly int _flushIntervalSeconds;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";

    public FileSpanExporter(IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry:FileExport");
        _outputDir = section["OutputDirectory"] ?? "./logs";
        _filePrefix = section["Traces:FilePrefix"] ?? "traces";
        _flushIntervalSeconds = section.GetValue("Traces:FlushIntervalSeconds", 5);

        Directory.CreateDirectory(_outputDir);
        EnsureWriter();
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();

                foreach (var activity in batch)
                {
                    var sb = new StringBuilder();
                    sb.Append($"[{activity.StartTimeUtc:yyyy-MM-dd HH:mm:ss.fff}] ");
                    sb.Append($"TraceId={activity.TraceId} ");
                    sb.Append($"SpanId={activity.SpanId} ");
                    sb.Append($"ParentId={activity.ParentSpanId} ");
                    sb.Append($"Name={activity.DisplayName} ");
                    sb.Append($"Kind={activity.Kind} ");
                    sb.Append($"Status={activity.Status} ");
                    sb.Append($"Duration={activity.Duration.TotalMilliseconds:F1}ms");

                    foreach (var tag in activity.TagObjects)
                    {
                        sb.Append($" {tag.Key}={tag.Value}");
                    }

                    foreach (var evt in activity.Events)
                    {
                        sb.Append($" | Event: {evt.Name} at {evt.Timestamp:HH:mm:ss.fff}");
                    }

                    _writer?.WriteLine(sb.ToString());
                }

                _writer?.Flush();
                return ExportResult.Success;
            }
            catch (Exception)
            {
                return ExportResult.Failure;
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (today == _currentDate && _writer != null) return;

        _writer?.Flush();
        _writer?.Dispose();

        _currentDate = today;
        var filePath = Path.Combine(_outputDir, $"{_filePrefix}-{_currentDate}.log");
        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8)
        {
            AutoFlush = false
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A simple file-based metric exporter that writes metric snapshots.
/// </summary>
public sealed class FileMetricExporter : BaseExporter<Metric>
{
    private readonly string _outputDir;
    private readonly string _filePrefix;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";

    public FileMetricExporter(IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry:FileExport");
        _outputDir = section["OutputDirectory"] ?? "./logs";
        _filePrefix = section["Metrics:FilePrefix"] ?? "metrics";

        Directory.CreateDirectory(_outputDir);
        EnsureWriter();
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();

                _writer?.WriteLine($"--- Metric Export at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC ---");

                foreach (var metric in batch)
                {
                    _writer?.Write(
                        $"  Metric: {metric.Name} | Type: {metric.MetricType} | Unit: {metric.Unit ?? "none"} | ");

                    foreach (ref readonly var point in metric.GetMetricPoints())
                    {
                        switch (metric.MetricType)
                        {
                            case MetricType.LongSum:
                            case MetricType.LongSumNonMonotonic:
                                _writer?.Write($"Value={point.GetSumLong()} ");
                                break;
                            case MetricType.DoubleSum:
                            case MetricType.DoubleSumNonMonotonic:
                                _writer?.Write($"Value={point.GetSumDouble():F2} ");
                                break;
                            case MetricType.LongGauge:
                                _writer?.Write($"Value={point.GetGaugeLastValueLong()} ");
                                break;
                            case MetricType.DoubleGauge:
                                _writer?.Write($"Value={point.GetGaugeLastValueDouble():F2} ");
                                break;
                            case MetricType.Histogram:
                                _writer?.Write($"Count={point.GetHistogramCount()} Sum={point.GetHistogramSum():F2} ");
                                break;
                        }

                        foreach (var tag in point.Tags)
                        {
                            _writer?.Write($"{tag.Key}={tag.Value} ");
                        }
                    }

                    _writer?.WriteLine();
                }

                _writer?.Flush();
                return ExportResult.Success;
            }
            catch (Exception)
            {
                return ExportResult.Failure;
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (today == _currentDate && _writer != null) return;

        _writer?.Flush();
        _writer?.Dispose();

        _currentDate = today;
        var filePath = Path.Combine(_outputDir, $"{_filePrefix}-{_currentDate}.log");
        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8)
        {
            AutoFlush = false
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
        base.Dispose(disposing);
    }
}
```

### Register the Custom Exporters (updated Program.cs)

Replace the Console exporter registrations in `Program.cs` with:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName, serviceVersion: serviceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = environment,
            ["host.name"] = System.Environment.MachineName
        }))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(AppActivitySource.Name)
            .AddHttpClientInstrumentation();

        if (tracesEnabled)
        {
            tracing.AddProcessor(new SimpleActivityExportProcessor(
                new FileSpanExporter(builder.Configuration)));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(AppMeter.Name)
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();

        if (metricsEnabled)
        {
            var exportInterval = builder.Configuration
                .GetValue("OTEL_METRIC_EXPORT_INTERVAL", 15000);

            metrics.AddReader(new PeriodicExportingMetricReader(
                new FileMetricExporter(builder.Configuration),
                exportIntervalMilliseconds: exportInterval));
        }
    });
```

---

## Worker.cs — Sample Instrumented Service

A background worker that produces traces, metrics, and logs for testing.

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OtelFileDemo;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    private static readonly Counter<long> ItemsProcessed =
        Program.AppMeter.CreateCounter<long>(
            "items.processed", "items", "Total items processed");

    private static readonly Histogram<double> ProcessingDuration =
        Program.AppMeter.CreateHistogram<double>(
            "processing.duration", "ms", "Time to process a single item");

    private static readonly UpDownCounter<int> ActiveJobs =
        Program.AppMeter.CreateUpDownCounter<int>(
            "jobs.active", "jobs", "Currently active processing jobs");

    private int _iteration;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started. Telemetry will be written to local files.");

        while (!stoppingToken.IsCancellationRequested)
        {
            _iteration++;

            using var activity = Program.AppActivitySource.StartActivity(
                "ProcessBatch", ActivityKind.Internal);

            activity?.SetTag("batch.iteration", _iteration);
            activity?.SetTag("batch.size", Random.Shared.Next(10, 100));

            ActiveJobs.Add(1);

            try
            {
                _logger.LogInformation(
                    "Processing batch {Iteration} at {Timestamp}",
                    _iteration, DateTimeOffset.UtcNow);

                await SimulateItemProcessing(stoppingToken);

                var batchSize = Random.Shared.Next(10, 100);
                ItemsProcessed.Add(batchSize,
                    new KeyValuePair<string, object?>("batch.type", "standard"));

                activity?.SetTag("batch.items_processed", batchSize);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation(
                    "Batch {Iteration} completed successfully. Processed {Count} items.",
                    _iteration, batchSize);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Batch {Iteration} was cancelled.", _iteration);
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch {Iteration} failed.", _iteration);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.RecordException(ex);
            }
            finally
            {
                ActiveJobs.Add(-1);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task SimulateItemProcessing(CancellationToken ct)
    {
        using var activity = Program.AppActivitySource.StartActivity(
            "ProcessSingleItem", ActivityKind.Internal);

        var stopwatch = Stopwatch.StartNew();
        var delayMs = Random.Shared.Next(50, 500);
        await Task.Delay(delayMs, ct);
        stopwatch.Stop();

        activity?.SetTag("item.processing_time_ms", stopwatch.ElapsedMilliseconds);
        ProcessingDuration.Record(stopwatch.Elapsed.TotalMilliseconds);

        _logger.LogDebug("Item processed in {ElapsedMs}ms",
            stopwatch.ElapsedMilliseconds);
    }
}
```

---

## Output File Structure

After running the app, your `./logs/` directory will look like this:

```
logs/
├── traces-2026-04-15.log          # OpenTelemetry trace spans
├── metrics-2026-04-15.log         # OpenTelemetry metric snapshots
├── app-20260415.log               # Serilog: human-readable logs
└── app-structured-20260415.log    # Serilog: compact JSON logs
```

---

## Sample Output

### traces-2026-04-15.log

```
[2026-04-15 20:50:12.345] TraceId=a1b2c3d4e5f678901234567890abcdef SpanId=1234567890abcdef ParentId=0000000000000000 Name=ProcessBatch Kind=Internal Status=Ok Duration=234.5ms batch.iteration=1 batch.size=42 batch.items_processed=42
[2026-04-15 20:50:12.456] TraceId=a1b2c3d4e5f678901234567890abcdef SpanId=abcdef1234567890 ParentId=1234567890abcdef Name=ProcessSingleItem Kind=Internal Status=Unset Duration=187.3ms item.processing_time_ms=187
```

### metrics-2026-04-15.log

```
--- Metric Export at 2026-04-15 20:50:27.000 UTC ---
  Metric: items.processed | Type: LongSum | Unit: items | Value=42 batch.type=standard
  Metric: processing.duration | Type: Histogram | Unit: ms | Count=1 Sum=187.30
  Metric: jobs.active | Type: LongSumNonMonotonic | Unit: jobs | Value=0
  Metric: process.cpu.time | Type: DoubleSum | Unit: s | Value=0.45 cpu.mode=user
  Metric: process.memory.usage | Type: LongGauge | Unit: By | Value=52428800
```

### app-20260415.log (Serilog — human-readable)

```
[2026-04-15 13:50:12.345 -07:00] [INF] [OtelFileDemo.Worker] Worker started. Telemetry will be written to local files.
[2026-04-15 13:50:12.456 -07:00] [INF] [OtelFileDemo.Worker] Processing batch 1 at 04/15/2026 20:50:12 +00:00
[2026-04-15 13:50:12.890 -07:00] [INF] [OtelFileDemo.Worker] Batch 1 completed successfully. Processed 42 items.
```

### app-structured-20260415.log (Serilog — compact JSON)

```json
{"@t":"2026-04-15T20:50:12.345Z","@mt":"Processing batch {Iteration} at {Timestamp}","Iteration":1,"Timestamp":"2026-04-15T20:50:12.345+00:00","SourceContext":"OtelFileDemo.Worker"}
{"@t":"2026-04-15T20:50:12.890Z","@mt":"Batch {Iteration} completed successfully. Processed {Count} items.","Iteration":1,"Count":42,"SourceContext":"OtelFileDemo.Worker"}
```

---

## Configuration Deep Dive

### Controlling what gets logged — appsettings.json knobs

| Setting Path | Controls | Typical Values |
|---|---|---|
| `Logging:LogLevel:Default` | Minimum log level for all categories | `Debug`, `Information`, `Warning` |
| `Logging:LogLevel:OpenTelemetry` | OTel SDK internal verbosity | `Warning` (prod), `Debug` (dev) |
| `OpenTelemetry:FileExport:Traces:Enabled` | Toggle trace file export | `true` / `false` |
| `OpenTelemetry:FileExport:Metrics:Enabled` | Toggle metric file export | `true` / `false` |
| `OpenTelemetry:FileExport:OutputDirectory` | Base directory for all log files | `./logs`, `/var/log/myapp` |
| `OpenTelemetry:FileExport:RetainedFileCountLimit` | Max rolling files to keep | `30` (one month) |
| `OTEL_TRACES_SAMPLER` | Sampling strategy | `always_on` for file export |
| `OTEL_METRIC_EXPORT_INTERVAL` | How often metrics are flushed (ms) | `15000` (dev), `60000` (prod) |
| `OTEL_BSP_SCHEDULE_DELAY` | How often trace batches are flushed (ms) | `5000` |
| `Serilog:WriteTo:0:Args:rollingInterval` | Log file rolling frequency | `Day`, `Hour` |

### Disabling signals at runtime

```json
{
  "OpenTelemetry": {
    "FileExport": {
      "Traces": { "Enabled": false },
      "Metrics": { "Enabled": true }
    }
  }
}
```

### Changing output in Production

**appsettings.Production.json:**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "OpenTelemetry": "Error"
    }
  },

  "OpenTelemetry": {
    "Environment": "production",
    "FileExport": {
      "OutputDirectory": "/var/log/otel-file-demo",
      "RetainedFileCountLimit": 90,
      "Traces": { "Enabled": true },
      "Metrics": { "Enabled": true }
    }
  },

  "OTEL_TRACES_SAMPLER": "parentbased_traceidratio",
  "OTEL_TRACES_SAMPLER_ARG": "0.1",
  "OTEL_METRIC_EXPORT_INTERVAL": "60000",
  "OTEL_BSP_SCHEDULE_DELAY": "10000",

  "Serilog": {
    "MinimumLevel": { "Default": "Warning" },
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "/var/log/otel-file-demo/app-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 90,
          "fileSizeLimitBytes": 524288000,
          "rollOnFileSizeLimit": true
        }
      }
    ]
  }
}
```

---

## Alternative: Serilog File Sink for Logs

If you prefer Serilog without OpenTelemetry log export (simpler, fewer dependencies), configure only Serilog for logs and OpenTelemetry for traces/metrics:

```csharp
// Program.cs — logs via Serilog only, no OTel log exporter
builder.Host.UseSerilog((context, loggerConfig) =>
{
    loggerConfig.ReadFrom.Configuration(context.Configuration);
});

// OpenTelemetry handles only traces and metrics
builder.Services.AddOpenTelemetry()
    .ConfigureResource(/* ... */)
    .WithTracing(/* ... custom file exporter ... */)
    .WithMetrics(/* ... custom file exporter ... */);

// Do NOT call builder.Logging.AddOpenTelemetry() in this scenario
```

**Benefit:** All log configuration lives in the `Serilog` section of `appsettings.json` with zero code changes needed for format, level, or rolling policy adjustments.

---

## Tips & Gotchas

### 1. Always flush on shutdown

The Generic Host calls `Dispose` on hosted services, but file writers need explicit flushing:

```csharp
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    traceWriter?.Flush();
    metricWriter?.Flush();
    Log.CloseAndFlush();
});
```

### 2. File locking on Windows

`StreamWriter` holds an exclusive lock. To read log files while the app runs, open with `FileShare.Read`:

```csharp
var fileStream = new FileStream(filePath, FileMode.Append,
    FileAccess.Write, FileShare.Read);
var writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = false };
```

### 3. Console exporter is for development

The `OpenTelemetry.Exporter.Console` package is not optimized for production. For high-throughput scenarios, use the custom `FileSpanExporter` / `FileMetricExporter` shown above.

### 4. Structured vs. plain text

- **Traces/Metrics:** The custom exporters write semi-structured text. For machine-parseable output, serialize to JSON using `System.Text.Json`.
- **Logs:** Serilog's `CompactJsonFormatter` produces newline-delimited JSON — ideal for log aggregation tools.

### 5. Correlating logs with traces

Serilog can enrich logs with trace context automatically:

```bash
dotnet add package Serilog.Enrichers.Span
```

```json
{
  "Serilog": {
    "Enrich": ["FromLogContext", "WithSpan"]
  }
}
```

This adds `TraceId` and `SpanId` to every log entry, letting you correlate file-based logs with file-based traces.

### 6. Log rotation cleanup

Serilog handles rotation via `RetainedFileCountLimit`. For the custom trace/metric exporters, add a cleanup routine:

```csharp
var cutoff = DateTime.UtcNow.AddDays(-30);
foreach (var file in Directory.GetFiles(outputDir, "traces-*.log"))
{
    if (File.GetLastWriteTimeUtc(file) < cutoff)
        File.Delete(file);
}
```
