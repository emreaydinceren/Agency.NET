I'll research the latest OpenTelemetry .NET configuration options and create a comprehensive markdown guide for you.

Your OpenTelemetry configuration guide is ready! The `.md` format isn't supported for direct file download, so here's the full markdown source you can save as `opentelemetry-appsettings-guide.md`:

```markdown
# Configuring OpenTelemetry in `appsettings.json` for .NET

A comprehensive guide to configuring traces, metrics, and logs for OpenTelemetry in .NET applications using `appsettings.json`.

---

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [How IConfiguration Integration Works](#how-iconfiguration-integration-works)
4. [NuGet Packages](#nuget-packages)
5. [Minimal Setup — Program.cs](#minimal-setup--programcs)
6. [Full appsettings.json Reference](#full-appsettingsjson-reference)
7. [OTLP Exporter Configuration](#otlp-exporter-configuration)
8. [Per-Signal Configuration](#per-signal-configuration)
   - [Traces](#traces)
   - [Metrics](#metrics)
   - [Logs](#logs)
9. [Batch Processor Options](#batch-processor-options)
10. [Attribute Limits](#attribute-limits)
11. [Metric Reader Options](#metric-reader-options)
12. [Sampler Configuration](#sampler-configuration)
13. [Resource Configuration](#resource-configuration)
14. [Enabled Metrics via Configuration](#enabled-metrics-via-configuration)
15. [Logging Levels for OTel Internals](#logging-levels-for-otel-internals)
16. [mTLS (Mutual TLS) Configuration](#mtls-mutual-tls-configuration)
17. [Experimental Features](#experimental-features)
18. [Environment-Specific Overrides](#environment-specific-overrides)
19. [Binding Options in Program.cs](#binding-options-in-programcs)
20. [Complete Production Example](#complete-production-example)
21. [Troubleshooting](#troubleshooting)
22. [Quick Reference Table](#quick-reference-table)

---

## Overview

The OpenTelemetry .NET SDK is built on top of `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.Options`. This means every `OTEL_*` environment variable defined by the OpenTelemetry specification can also be set as a key in `appsettings.json`.

**Configuration precedence (lowest → highest):**

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. Environment variables
4. Command-line arguments
5. Programmatic code (`Action<TOptions>` delegates)

This guide focuses on the `appsettings.json` layer so you can manage configuration declaratively, per environment, and without recompilation.

---

## Prerequisites

- .NET 8.0 or later (recommended; .NET 6+ supported)
- An OTLP-compatible backend (e.g., OpenTelemetry Collector, Jaeger, Grafana Tempo, Azure Monitor)
- Familiarity with ASP.NET Core's `IConfiguration` system

---

## How IConfiguration Integration Works

When you call `builder.Services.AddOpenTelemetry()`, the SDK automatically reads from the host's `IConfiguration` instance. That instance is assembled from all registered providers — including JSON files. This means any `OTEL_*` key you place in `appsettings.json` is picked up exactly as if it were an environment variable.

**Key rule:** Use the `:` (colon) separator for nested configuration in JSON. For example, the environment variable `OTEL_EXPORTER_OTLP_ENDPOINT` becomes:

```json
{
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317"
}
```

For structured/nested options bound via `Configure<T>`, you use conventional JSON nesting (see sections below).

---

## NuGet Packages

Install the packages relevant to your scenario:

```bash
# Core hosting integration
dotnet add package OpenTelemetry.Extensions.Hosting

# OTLP exporter (traces, metrics, logs)
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol

# ASP.NET Core instrumentation
dotnet add package OpenTelemetry.Instrumentation.AspNetCore

# HTTP client instrumentation
dotnet add package OpenTelemetry.Instrumentation.Http

# (Optional) Console exporter for local debugging
dotnet add package OpenTelemetry.Exporter.Console
```

---

## Minimal Setup — Program.cs

Before `appsettings.json` configuration takes effect, you need to wire OpenTelemetry into the host. The simplest approach uses the cross-cutting `UseOtlpExporter()` extension (available since `1.8.0-beta.1`):

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("my-service"))
    .UseOtlpExporter()                       // Registers OTLP for all signals
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

// Logging is configured separately
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
});

var app = builder.Build();
app.Run();
```

> **Note:** `UseOtlpExporter()` enables OTLP export for all three signals. It cannot be combined with per-signal `AddOtlpExporter()` calls — doing so throws a `NotSupportedException`.

**Alternative per-signal registration** (when you need different exporters per signal):

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("my-service"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())                  // Traces → OTLP
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());                 // Metrics → OTLP

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeScopes = true;
    logging.AddOtlpExporter();               // Logs → OTLP
});
```

---

## Full appsettings.json Reference

Below is a comprehensive `appsettings.json` showing every major configuration area. **You do not need all of these** — include only the sections relevant to your application.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "OpenTelemetry": "Warning"
    },
    "OpenTelemetry": {
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true
    }
  },

  "OTEL_SERVICE_NAME": "my-dotnet-service",
  "OTEL_RESOURCE_ATTRIBUTES": "deployment.environment=production,service.version=1.2.0",

  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://otel-collector:4317",
  "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
  "OTEL_EXPORTER_OTLP_HEADERS": "api-key=your-key-here",
  "OTEL_EXPORTER_OTLP_TIMEOUT": "10000",

  "OTEL_TRACES_SAMPLER": "parentbased_traceidratio",
  "OTEL_TRACES_SAMPLER_ARG": "0.5",

  "OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_ATTRIBUTE_COUNT_LIMIT": "128",

  "OTEL_SPAN_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_SPAN_ATTRIBUTE_COUNT_LIMIT": "128",
  "OTEL_SPAN_EVENT_COUNT_LIMIT": "128",
  "OTEL_SPAN_LINK_COUNT_LIMIT": "128",
  "OTEL_EVENT_ATTRIBUTE_COUNT_LIMIT": "128",
  "OTEL_LINK_ATTRIBUTE_COUNT_LIMIT": "128",

  "OTEL_LOGRECORD_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_LOGRECORD_ATTRIBUTE_COUNT_LIMIT": "128",

  "OTEL_BSP_SCHEDULE_DELAY": "5000",
  "OTEL_BSP_EXPORT_TIMEOUT": "30000",
  "OTEL_BSP_MAX_QUEUE_SIZE": "2048",
  "OTEL_BSP_MAX_EXPORT_BATCH_SIZE": "512",

  "OTEL_BLRP_SCHEDULE_DELAY": "5000",
  "OTEL_BLRP_EXPORT_TIMEOUT": "30000",
  "OTEL_BLRP_MAX_QUEUE_SIZE": "2048",
  "OTEL_BLRP_MAX_EXPORT_BATCH_SIZE": "512",

  "OTEL_METRIC_EXPORT_INTERVAL": "60000",
  "OTEL_METRIC_EXPORT_TIMEOUT": "30000",
  "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE": "cumulative",
  "OTEL_EXPORTER_OTLP_METRICS_DEFAULT_HISTOGRAM_AGGREGATION": "explicit_bucket_histogram",

  "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": "http://traces-collector:4317",
  "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://metrics-collector:4317",
  "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT": "http://logs-collector:4317",

  "OTEL_EXPORTER_OTLP_CERTIFICATE": "/certs/ca.pem",
  "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE": "/certs/client.pem",
  "OTEL_EXPORTER_OTLP_CLIENT_KEY": "/certs/client-key.pem",

  "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
  "OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES": "true"
}
```

---

## OTLP Exporter Configuration

The core OTLP exporter settings control where and how telemetry is shipped.

### All Signals (Global Defaults)

| appsettings.json Key | Maps to `OtlpExporterOptions` | Default | Description |
|---|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `Endpoint` | `http://localhost:4317` (gRPC) / `http://localhost:4318` (HTTP) | Collector endpoint URI |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `Protocol` | `grpc` | Transport protocol: `grpc` or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Headers` | _(none)_ | Comma-separated `key=value` pairs |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | `TimeoutMilliseconds` | `10000` | Export timeout in ms |

### Protocol Considerations

- **gRPC** (`grpc`): Uses port `4317` by default. The endpoint is the base URI.
- **HTTP/Protobuf** (`http/protobuf`): Uses port `4318` by default. When using `UseOtlpExporter()`, signal-specific paths (e.g., `/v1/traces`) are appended automatically. When using per-signal `AddOtlpExporter()`, you must provide the full path.

**Example — HTTP/Protobuf with full path (per-signal registration):**

```json
{
  "OTEL_EXPORTER_OTLP_PROTOCOL": "http/protobuf",
  "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": "http://collector:4318/v1/traces",
  "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://collector:4318/v1/metrics",
  "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT": "http://collector:4318/v1/logs"
}
```

---

## Per-Signal Configuration

When using `UseOtlpExporter()`, you can override global defaults for each signal. These keys take precedence over the global `OTEL_EXPORTER_OTLP_*` keys.

> **Important:** The per-signal `OTEL_EXPORTER_OTLP_{SIGNAL}_*` keys are only supported with `UseOtlpExporter()`. They are **not** supported with per-signal `AddOtlpExporter()` calls.

### Traces

| appsettings.json Key | Description |
|---|---|
| `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | Override endpoint for traces |
| `OTEL_EXPORTER_OTLP_TRACES_HEADERS` | Override headers for traces |
| `OTEL_EXPORTER_OTLP_TRACES_TIMEOUT` | Override timeout for traces |
| `OTEL_EXPORTER_OTLP_TRACES_PROTOCOL` | Override protocol for traces |

### Metrics

| appsettings.json Key | Description |
|---|---|
| `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` | Override endpoint for metrics |
| `OTEL_EXPORTER_OTLP_METRICS_HEADERS` | Override headers for metrics |
| `OTEL_EXPORTER_OTLP_METRICS_TIMEOUT` | Override timeout for metrics |
| `OTEL_EXPORTER_OTLP_METRICS_PROTOCOL` | Override protocol for metrics |
| `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE` | `cumulative` (default) or `delta` |
| `OTEL_EXPORTER_OTLP_METRICS_DEFAULT_HISTOGRAM_AGGREGATION` | `explicit_bucket_histogram` (default) or `base2_exponential_bucket_histogram` |

### Logs

| appsettings.json Key | Description |
|---|---|
| `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` | Override endpoint for logs |
| `OTEL_EXPORTER_OTLP_LOGS_HEADERS` | Override headers for logs |
| `OTEL_EXPORTER_OTLP_LOGS_TIMEOUT` | Override timeout for logs |
| `OTEL_EXPORTER_OTLP_LOGS_PROTOCOL` | Override protocol for logs |

---

## Batch Processor Options

The batch processor buffers telemetry and exports it in batches for efficiency.

### Traces (BatchExportActivityProcessorOptions)

| appsettings.json Key | Property | Default |
|---|---|---|
| `OTEL_BSP_SCHEDULE_DELAY` | `ScheduledDelayMilliseconds` | `5000` |
| `OTEL_BSP_EXPORT_TIMEOUT` | `ExporterTimeoutMilliseconds` | `30000` |
| `OTEL_BSP_MAX_QUEUE_SIZE` | `MaxQueueSize` | `2048` |
| `OTEL_BSP_MAX_EXPORT_BATCH_SIZE` | `MaxExportBatchSize` | `512` |

### Logs (BatchExportLogRecordProcessorOptions)

| appsettings.json Key | Property | Default |
|---|---|---|
| `OTEL_BLRP_SCHEDULE_DELAY` | `ScheduledDelayMilliseconds` | `5000` |
| `OTEL_BLRP_EXPORT_TIMEOUT` | `ExporterTimeoutMilliseconds` | `30000` |
| `OTEL_BLRP_MAX_QUEUE_SIZE` | `MaxQueueSize` | `2048` |
| `OTEL_BLRP_MAX_EXPORT_BATCH_SIZE` | `MaxExportBatchSize` | `512` |

**Tuning guidance:**

- **High-throughput services:** Increase `MAX_QUEUE_SIZE` and `MAX_EXPORT_BATCH_SIZE` to avoid dropped spans/logs.
- **Low-latency export:** Decrease `SCHEDULE_DELAY` (e.g., `1000` ms) at the cost of more frequent exports.
- **Resource-constrained:** Keep defaults or reduce batch sizes.

---

## Attribute Limits

Control the maximum size and count of attributes attached to telemetry items.

### Global Defaults

```json
{
  "OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_ATTRIBUTE_COUNT_LIMIT": "128"
}
```

### Span-Specific Overrides

```json
{
  "OTEL_SPAN_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_SPAN_ATTRIBUTE_COUNT_LIMIT": "128",
  "OTEL_SPAN_EVENT_COUNT_LIMIT": "128",
  "OTEL_SPAN_LINK_COUNT_LIMIT": "128",
  "OTEL_EVENT_ATTRIBUTE_COUNT_LIMIT": "128",
  "OTEL_LINK_ATTRIBUTE_COUNT_LIMIT": "128"
}
```

### Log-Specific Overrides

```json
{
  "OTEL_LOGRECORD_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",
  "OTEL_LOGRECORD_ATTRIBUTE_COUNT_LIMIT": "128"
}
```

Signal-specific limits take precedence over global limits.

---

## Metric Reader Options

Control how often metrics are collected and exported.

| appsettings.json Key | Property | Default |
|---|---|---|
| `OTEL_METRIC_EXPORT_INTERVAL` | `ExportIntervalMilliseconds` | `60000` (60 sec) |
| `OTEL_METRIC_EXPORT_TIMEOUT` | `ExportTimeoutMilliseconds` | `30000` (30 sec) |

**Example — faster metric export for dashboards:**

```json
{
  "OTEL_METRIC_EXPORT_INTERVAL": "15000",
  "OTEL_METRIC_EXPORT_TIMEOUT": "10000"
}
```

---

## Sampler Configuration

Sampling controls which traces are recorded and exported.

| appsettings.json Key | Description |
|---|---|
| `OTEL_TRACES_SAMPLER` | Sampler type |
| `OTEL_TRACES_SAMPLER_ARG` | Sampler argument (e.g., ratio) |

### Supported Sampler Values

| Value | Behavior |
|---|---|
| `always_on` | Record every trace (default) |
| `always_off` | Record no traces |
| `traceidratio` | Sample based on trace ID ratio |
| `parentbased_always_on` | Follow parent decision; root spans always sampled |
| `parentbased_always_off` | Follow parent decision; root spans never sampled |
| `parentbased_traceidratio` | Follow parent decision; root spans sampled by ratio |

**Example — sample 25% of root traces, respect parent decisions:**

```json
{
  "OTEL_TRACES_SAMPLER": "parentbased_traceidratio",
  "OTEL_TRACES_SAMPLER_ARG": "0.25"
}
```

---

## Resource Configuration

Resources identify your service in the telemetry backend.

```json
{
  "OTEL_SERVICE_NAME": "order-api",
  "OTEL_RESOURCE_ATTRIBUTES": "deployment.environment=staging,service.version=2.1.0,service.namespace=ecommerce"
}
```

`OTEL_RESOURCE_ATTRIBUTES` is a comma-separated list of `key=value` pairs conforming to OpenTelemetry Resource Semantic Conventions.

Common attributes:

| Attribute | Example | Description |
|---|---|---|
| `service.name` | `order-api` | Logical name of the service |
| `service.version` | `2.1.0` | Version of the service |
| `service.namespace` | `ecommerce` | Namespace grouping services |
| `deployment.environment` | `production` | Deployment target |
| `host.name` | `server-01` | Hostname |

> **Note:** `OTEL_SERVICE_NAME` is a shortcut for setting `service.name`. If both are provided, `OTEL_SERVICE_NAME` takes precedence.

---

## Enabled Metrics via Configuration

With `Microsoft.Extensions.Hosting` v8.0+ (standard in ASP.NET Core 8+), you can enable specific meters via configuration without code changes:

```json
{
  "Metrics": {
    "EnabledMetrics": {
      "Microsoft.AspNetCore.*": true,
      "System.Net.Http.*": true,
      "System.Runtime.*": true,
      "MyCompany.MyApp.*": true
    }
  }
}
```

This is equivalent to calling `.AddMeter("Microsoft.AspNetCore.*")` in code but managed entirely through configuration.

---

## Logging Levels for OTel Internals

Control the verbosity of OpenTelemetry's own diagnostic output through the standard `Logging` section:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "OpenTelemetry": "Warning"
    },
    "OpenTelemetry": {
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true
    }
  }
}
```

| Setting | Type | Default | Description |
|---|---|---|---|
| `IncludeFormattedMessage` | `bool` | `false` | Include the rendered log message string |
| `IncludeScopes` | `bool` | `false` | Export `ILogger` scope values as attributes |
| `ParseStateValues` | `bool` | `false` | Parse structured log state into individual attributes |

> **Recommendation:** Set `IncludeFormattedMessage` to `true` in most cases — backends like Grafana Loki and Azure Monitor expect the rendered message.

---

## mTLS (Mutual TLS) Configuration

For secure communication with your collector (.NET 8.0+ only):

```json
{
  "OTEL_EXPORTER_OTLP_CERTIFICATE": "/certs/ca.pem",
  "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE": "/certs/client.pem",
  "OTEL_EXPORTER_OTLP_CLIENT_KEY": "/certs/client-key.pem"
}
```

| Key | Description |
|---|---|
| `OTEL_EXPORTER_OTLP_CERTIFICATE` | Path to the CA certificate file (PEM format) |
| `OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE` | Path to the client certificate file (PEM format) |
| `OTEL_EXPORTER_OTLP_CLIENT_KEY` | Path to the client private key file (PEM format) |

---

## Experimental Features

These features are opt-in and may change in future releases.

```json
{
  "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",
  "OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES": "true"
}
```

| Key | Values | Description |
|---|---|---|
| `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY` | `in_memory`, `disk` | Retry strategy for transient export failures. `disk` persists to temp directory |
| `OTEL_DOTNET_EXPERIMENTAL_OTLP_DISK_RETRY_DIRECTORY_PATH` | File path | Custom directory for disk-based retry storage |
| `OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES` | `true`/`false` | Export `LogRecord.EventId` as `logrecord.event.id` and `logrecord.event.name` |

---

## Environment-Specific Overrides

Use `appsettings.{Environment}.json` to override settings per environment.

**appsettings.Development.json:**

```json
{
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317",
  "OTEL_TRACES_SAMPLER": "always_on",
  "OTEL_METRIC_EXPORT_INTERVAL": "10000",

  "Logging": {
    "LogLevel": {
      "OpenTelemetry": "Debug"
    }
  }
}
```

**appsettings.Production.json:**

```json
{
  "OTEL_EXPORTER_OTLP_ENDPOINT": "https://otel-collector.internal:4317",
  "OTEL_EXPORTER_OTLP_HEADERS": "Authorization=Bearer <token>",
  "OTEL_TRACES_SAMPLER": "parentbased_traceidratio",
  "OTEL_TRACES_SAMPLER_ARG": "0.1",

  "OTEL_BSP_MAX_QUEUE_SIZE": "4096",
  "OTEL_BSP_MAX_EXPORT_BATCH_SIZE": "1024",

  "OTEL_EXPORTER_OTLP_CERTIFICATE": "/certs/ca.pem",
  "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE": "/certs/client.pem",
  "OTEL_EXPORTER_OTLP_CLIENT_KEY": "/certs/client-key.pem",

  "Logging": {
    "LogLevel": {
      "OpenTelemetry": "Warning"
    }
  }
}
```

---

## Binding Options in Program.cs

For structured/nested configuration (rather than flat `OTEL_*` keys), you can bind options classes to custom JSON sections using the `Configure<T>` pattern.

### Bind OtlpExporterOptions (Global)

```json
{
  "OpenTelemetry": {
    "Otlp": {
      "Endpoint": "http://collector:4317",
      "Protocol": "grpc",
      "Headers": "api-key=abc123",
      "TimeoutMilliseconds": 15000
    }
  }
}
```

```csharp
builder.Services.Configure<OtlpExporterOptions>(
    builder.Configuration.GetSection("OpenTelemetry:Otlp"));
```

### Bind Per-Signal Options (Named)

```json
{
  "OpenTelemetry": {
    "Tracing": {
      "Otlp": {
        "Endpoint": "http://trace-collector:4317",
        "TimeoutMilliseconds": 10000
      }
    },
    "Metrics": {
      "Otlp": {
        "Endpoint": "http://metrics-collector:4317"
      }
    },
    "Logging": {
      "Otlp": {
        "Endpoint": "http://log-collector:4317"
      },
      "BatchExportProcessorOptions": {
        "ScheduledDelayMilliseconds": 2000,
        "MaxExportBatchSize": 5000
      }
    }
  }
}
```

```csharp
// Bind per-signal OTLP options using named options
builder.Services.Configure<OtlpExporterOptions>("tracing",
    builder.Configuration.GetSection("OpenTelemetry:Tracing:Otlp"));
builder.Services.Configure<OtlpExporterOptions>("metrics",
    builder.Configuration.GetSection("OpenTelemetry:Metrics:Otlp"));
builder.Services.Configure<OtlpExporterOptions>("logging",
    builder.Configuration.GetSection("OpenTelemetry:Logging:Otlp"));

// Bind log processor options
builder.Services.Configure<LogRecordExportProcessorOptions>(
    builder.Configuration.GetSection("OpenTelemetry:Logging"));

// Bind metric reader options
builder.Services.Configure<MetricReaderOptions>(
    builder.Configuration.GetSection("OpenTelemetry:Metrics"));

// Register exporters using matching names
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddOtlpExporter("tracing", configure: null))
    .WithMetrics(m => m.AddOtlpExporter("metrics", configure: null));

builder.Logging.AddOpenTelemetry(l =>
    l.AddOtlpExporter("logging", options => { }));
```

> **Tip:** Named options let you send traces, metrics, and logs to different collector endpoints — useful in architectures with specialized telemetry pipelines.

---

## Complete Production Example

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      "OpenTelemetry": "Warning"
    },
    "OpenTelemetry": {
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true
    }
  },

  "AllowedHosts": "*",

  "OTEL_SERVICE_NAME": "order-api",
  "OTEL_RESOURCE_ATTRIBUTES": "deployment.environment=production,service.version=1.0.0,service.namespace=ecommerce",

  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://otel-collector:4317",
  "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
  "OTEL_EXPORTER_OTLP_TIMEOUT": "10000",

  "OTEL_TRACES_SAMPLER": "parentbased_traceidratio",
  "OTEL_TRACES_SAMPLER_ARG": "0.1",

  "OTEL_BSP_SCHEDULE_DELAY": "5000",
  "OTEL_BSP_MAX_QUEUE_SIZE": "4096",
  "OTEL_BSP_MAX_EXPORT_BATCH_SIZE": "1024",

  "OTEL_BLRP_SCHEDULE_DELAY": "5000",
  "OTEL_BLRP_MAX_QUEUE_SIZE": "4096",
  "OTEL_BLRP_MAX_EXPORT_BATCH_SIZE": "1024",

  "OTEL_METRIC_EXPORT_INTERVAL": "60000",
  "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE": "cumulative",

  "OTEL_ATTRIBUTE_COUNT_LIMIT": "128",
  "OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT": "4096",

  "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory",

  "Metrics": {
    "EnabledMetrics": {
      "Microsoft.AspNetCore.*": true,
      "System.Net.Http.*": true,
      "System.Runtime.*": true,
      "MyCompany.OrderApi.*": true
    }
  }
}
```

### Program.cs

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry reads from IConfiguration automatically.
// appsettings.json keys like OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_TRACES_SAMPLER, etc.
// are resolved without any additional code.

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? "unknown",
            serviceVersion: typeof(Program).Assembly
                .GetName().Version?.ToString() ?? "0.0.0"))
    .UseOtlpExporter()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("MyCompany.OrderApi"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("MyCompany.OrderApi"));

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage =
        builder.Configuration.GetValue("Logging:OpenTelemetry:IncludeFormattedMessage", true);
    logging.IncludeScopes =
        builder.Configuration.GetValue("Logging:OpenTelemetry:IncludeScopes", true);
});

var app = builder.Build();
app.MapControllers();
app.Run();
```

---

## Troubleshooting

### Enable OTel Diagnostic Logging

Set the OpenTelemetry log level to `Debug` or `Trace` in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "OpenTelemetry": "Debug"
    }
  }
}
```

The SDK uses an `EventSource` named `"OpenTelemetry-Exporter-OpenTelemetryProtocol"` for internal diagnostics.

### Common Issues

| Symptom | Likely Cause | Fix |
|---|---|---|
| No telemetry arriving at collector | Wrong endpoint or protocol mismatch | Verify `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_PROTOCOL` match your collector config |
| `NotSupportedException` on startup | Mixed use of `UseOtlpExporter()` and `AddOtlpExporter()` | Use one approach or the other, not both |
| Traces appear but metrics/logs don't | Missing instrumentation or meter registration | Ensure `.WithMetrics()` / `.AddOpenTelemetry(logging)` are called, and meters/sources are registered |
| Attributes truncated | Attribute limits too low | Increase `OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT` and `OTEL_ATTRIBUTE_COUNT_LIMIT` |
| High memory usage | Queue too large or export failing silently | Check `MAX_QUEUE_SIZE` settings; enable retry with `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY` |

---

## Quick Reference Table

All `appsettings.json` keys at a glance:

| Key | Signal | Default | Notes |
|---|---|---|---|
| `OTEL_SERVICE_NAME` | All | _(auto-detected)_ | Shortcut for `service.name` resource attribute |
| `OTEL_RESOURCE_ATTRIBUTES` | All | _(none)_ | Comma-separated `key=value` pairs |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | All | `localhost:4317` | Collector URI |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | All | `grpc` | `grpc` or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | All | _(none)_ | Auth headers |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | All | `10000` | Export timeout (ms) |
| `OTEL_TRACES_SAMPLER` | Traces | `parentbased_always_on` | Sampler type |
| `OTEL_TRACES_SAMPLER_ARG` | Traces | _(none)_ | Sampler argument |
| `OTEL_BSP_SCHEDULE_DELAY` | Traces | `5000` | Batch delay (ms) |
| `OTEL_BSP_MAX_QUEUE_SIZE` | Traces | `2048` | Max queued spans |
| `OTEL_BSP_MAX_EXPORT_BATCH_SIZE` | Traces | `512` | Max batch size |
| `OTEL_BSP_EXPORT_TIMEOUT` | Traces | `30000` | Batch export timeout (ms) |
| `OTEL_BLRP_SCHEDULE_DELAY` | Logs | `5000` | Log batch delay (ms) |
| `OTEL_BLRP_MAX_QUEUE_SIZE` | Logs | `2048` | Max queued log records |
| `OTEL_BLRP_MAX_EXPORT_BATCH_SIZE` | Logs | `512` | Max log batch size |
| `OTEL_BLRP_EXPORT_TIMEOUT` | Logs | `30000` | Log batch timeout (ms) |
| `OTEL_METRIC_EXPORT_INTERVAL` | Metrics | `60000` | Metric export interval (ms) |
| `OTEL_METRIC_EXPORT_TIMEOUT` | Metrics | `30000` | Metric export timeout (ms) |
| `OTEL_ATTRIBUTE_COUNT_LIMIT` | All | `128` | Max attributes per item |
| `OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT` | All | _(unlimited)_ | Max attribute value length |

---

## Further Reading

- [OpenTelemetry .NET — Official Repository](https://github.com/open-telemetry/opentelemetry-dotnet)
- [OTLP Exporter README](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
- [OpenTelemetry .NET — Getting Started (ASP.NET Core)](https://opentelemetry.io/docs/languages/dotnet/getting-started/)
- [OpenTelemetry Specification — Configuration](https://opentelemetry.io/docs/specs/otel/configuration/)
- [OpenTelemetry Resource Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/resource/)

---

*Last updated: April 2026*
```

The guide covers **22 sections** including:

- **Core IConfiguration integration** — how `OTEL_*` keys in JSON map directly to SDK options   [deepwiki.com](https://deepwiki.com/open-telemetry/opentelemetry-dotnet/5.1-environment-variables-(otel_*-settings))  [deepwiki.com](https://deepwiki.com/open-telemetry/opentelemetry-dotnet/4-configuration-system)
- **Two Program.cs patterns** — `UseOtlpExporter()` (cross-cutting) vs. per-signal `AddOtlpExporter()`, with the key constraint that they can't be mixed   [github.com](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
- **Full appsettings.json reference** with every configurable key, defaults, and descriptions
- **Per-signal overrides** for sending traces, metrics, and logs to different endpoints   [github.com](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
- **Named options binding** via `Configure<T>` for structured JSON sections   [github.com](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
- **Production-ready examples** with environment-specific overrides, sampling, batch tuning, mTLS, and metrics enablement
- **Troubleshooting matrix** for the most common failure scenarios

Copy the code block above and save it as `opentelemetry-appsettings-guide.md` in your project.