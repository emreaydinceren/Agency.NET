namespace Agency.Agentic.Console.Telemetry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

/// <summary>
/// Extension methods for registering OpenTelemetry file exporters and Serilog structured
/// logging into an <see cref="IServiceCollection"/> without a Generic Host.
/// </summary>
internal static class TelemetryServiceCollectionExtensions
{
    private const string AgencyWildcard = "Agency.*";

    /// <summary>
    /// Adds OpenTelemetry file exporters for traces and metrics, and a Serilog-backed
    /// <see cref="ILoggerFactory"/> for structured log files. Configuration is read from
    /// the <c>OpenTelemetry</c> section of <paramref name="configuration"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    internal static IServiceCollection AddTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        TelemetryOptions options = new();
        configuration.GetSection("OpenTelemetry").Bind(options);

        Directory.CreateDirectory(options.FileExport.OutputDirectory);

        ResourceBuilder resource = ResourceBuilder.CreateDefault()
            .AddService(options.ServiceName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["host.name"] = Environment.MachineName,
            });

        AddTracerProvider(services, options, resource);
        AddMeterProvider(services, options, resource);
        AddLogging(services, options);

        return services;
    }

    private static void AddTracerProvider(
        IServiceCollection services,
        TelemetryOptions options,
        ResourceBuilder resource)
    {
        if (!options.FileExport.Traces.Enabled)
        {
            return;
        }

        double ratio = Math.Clamp(options.FileExport.Traces.SamplingRatio, 0.0, 1.0);
        Sampler sampler = ratio >= 1.0
            ? new AlwaysOnSampler()
            : new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio));

        TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .SetSampler(sampler)
            .AddSource(AgencyWildcard)
            .AddProcessor(new SimpleActivityExportProcessor(new FileSpanExporter(options.FileExport)))
            .Build()
            ?? throw new InvalidOperationException("TracerProvider could not be built.");

        services.AddSingleton(tracerProvider);
    }

    private static void AddMeterProvider(
        IServiceCollection services,
        TelemetryOptions options,
        ResourceBuilder resource)
    {
        if (!options.FileExport.Metrics.Enabled)
        {
            return;
        }

        MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(AgencyWildcard)
            .AddReader(new PeriodicExportingMetricReader(
                new FileMetricExporter(options.FileExport),
                exportIntervalMilliseconds: options.FileExport.Metrics.ExportIntervalMs))
            .Build()
            ?? throw new InvalidOperationException("MeterProvider could not be built.");

        services.AddSingleton(meterProvider);
    }

    private static void AddLogging(
        IServiceCollection services,
        TelemetryOptions options)
    {
        if (!options.FileExport.Logs.Enabled)
        {
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            return;
        }

        LogEventLevel logLevel = Enum.TryParse<LogEventLevel>(
            options.FileExport.Logs.MinimumLevel, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : LogEventLevel.Information;

        string logPath = Path.Combine(
            options.FileExport.OutputDirectory,
            $"{options.FileExport.Logs.FilePrefix}-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logPath,
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 100 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(ToMsLogLevel(logLevel));
            b.AddProvider(new SerilogLoggerProvider(Log.Logger, dispose: false));
        });
    }

    private static LogLevel ToMsLogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose     => LogLevel.Trace,
        LogEventLevel.Debug       => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning     => LogLevel.Warning,
        LogEventLevel.Error       => LogLevel.Error,
        LogEventLevel.Fatal       => LogLevel.Critical,
        _                         => LogLevel.Information,
    };
}
