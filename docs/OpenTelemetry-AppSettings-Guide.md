# `.NET` console app guide: nested `OpenTelemetry` section mapped to `OTEL_*`

This version only uses the nested `OpenTelemetry` configuration model and includes logging.

> Important: the .NET OpenTelemetry SDK does not provide a direct file exporter for traces/metrics/logs. To keep **all destinations on file system**, send OTLP to a local OpenTelemetry Collector and configure the collector to write files.

## 1) `appsettings.json` (nested `OpenTelemetry` only)

```json
{
  "OpenTelemetry": {
    "ServiceName": "agency-console",
    "ResourceAttributes": "service.version=1.0.0,deployment.environment=Development,host.name=local",
    "Signals": {
      "Traces": true,
      "Metrics": true,
      "Logs": true
    },
    "Otlp": {
      "Endpoint": "http://localhost:4317",
      "Protocol": "grpc",
      "Headers": ""
    },
    "Tracing": {
      "Sources": [ "Agency.Agentic.Agent", "Agency.Agentic.Models" ],
      "Sampler": "always_on"
    },
    "Metrics": {
      "Meters": [ "Agency.Agentic.Agent", "Agency.Agentic.Models" ],
      "Runtime": true,
      "Process": true
    },
    "Logging": {
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true
    }
  }
}
```

## 2) More nested JSON examples

### A) Development profile

`appsettings.Development.json`:

```json
{
  "OpenTelemetry": {
    "ServiceName": "agency-console-dev",
    "ResourceAttributes": "service.version=1.0.0,deployment.environment=Development",
    "Otlp": {
      "Endpoint": "http://localhost:4317",
      "Protocol": "grpc"
    },
    "Tracing": {
      "Sampler": "always_on"
    }
  }
}
```

### B) Production profile

`appsettings.Production.json`:

```json
{
  "OpenTelemetry": {
    "ServiceName": "agency-console",
    "ResourceAttributes": "service.version=1.0.0,deployment.environment=Production",
    "Otlp": {
      "Endpoint": "http://localhost:4318",
      "Protocol": "http/protobuf",
      "Headers": "x-tenant-id=agency"
    },
    "Tracing": {
      "Sampler": "parentbased_traceidratio",
      "SamplerArg": "0.1"
    }
  }
}
```

### C) Disable metrics, keep traces + logs

```json
{
  "OpenTelemetry": {
    "Signals": {
      "Traces": true,
      "Metrics": false,
      "Logs": true
    }
  }
}
```

### D) File system destinations (paths) declared in app config

These are collector output file targets your app and ops can share in one config model:

```json
{
  "OpenTelemetry": {
    "FileDestinations": {
      "Traces": "./telemetry/traces.jsonl",
      "Metrics": "./telemetry/metrics.jsonl",
      "Logs": "./telemetry/logs.jsonl"
    }
  }
}
```

## 3) Minimal `Program.cs` bridge (nested section -> `OTEL_*`)

```csharp
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

var otel = builder.Configuration.GetSection("OpenTelemetry");

void SetOtel(string key, string? value)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        Environment.SetEnvironmentVariable(key, value);
    }
}

SetOtel("OTEL_SERVICE_NAME", otel["ServiceName"]);
SetOtel("OTEL_RESOURCE_ATTRIBUTES", otel["ResourceAttributes"]);
SetOtel("OTEL_EXPORTER_OTLP_ENDPOINT", otel["Otlp:Endpoint"]);
SetOtel("OTEL_EXPORTER_OTLP_PROTOCOL", otel["Otlp:Protocol"]);
SetOtel("OTEL_EXPORTER_OTLP_HEADERS", otel["Otlp:Headers"]);
SetOtel("OTEL_TRACES_SAMPLER", otel["Tracing:Sampler"]);
SetOtel("OTEL_TRACES_SAMPLER_ARG", otel["Tracing:SamplerArg"]);

bool tracesEnabled = otel.GetValue<bool?>("Signals:Traces") ?? true;
bool metricsEnabled = otel.GetValue<bool?>("Signals:Metrics") ?? true;
bool logsEnabled = otel.GetValue<bool?>("Signals:Logs") ?? true;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        if (!tracesEnabled) return;

        tracing
            .AddSource("Agency.Agentic.Agent", "Agency.Agentic.Models")
            .AddHttpClientInstrumentation()
            .AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        if (!metricsEnabled) return;

        metrics
            .AddMeter("Agency.Agentic.Agent", "Agency.Agentic.Models")
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter();
    });

if (logsEnabled)
{
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = otel.GetValue<bool?>("Logging:IncludeFormattedMessage") ?? true;
        logging.IncludeScopes = otel.GetValue<bool?>("Logging:IncludeScopes") ?? true;
        logging.ParseStateValues = otel.GetValue<bool?>("Logging:ParseStateValues") ?? true;
        logging.AddOtlpExporter();
    });
}

using IHost host = builder.Build();
await host.RunAsync();
```

## 4) Collector file system output example

`otel-collector-config.yaml`:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
      http:

processors:
  batch:

exporters:
  file/traces:
    path: ./telemetry/traces.jsonl
  file/metrics:
    path: ./telemetry/metrics.jsonl
  file/logs:
    path: ./telemetry/logs.jsonl

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [file/traces]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [file/metrics]
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [file/logs]
```

## 5) Quick validation

- Create `./telemetry/` folder.
- Start collector with the file exporter config.
- Run the console app.
- Confirm files are populated:
  - `./telemetry/traces.jsonl`
  - `./telemetry/metrics.jsonl`
  - `./telemetry/logs.jsonl`
