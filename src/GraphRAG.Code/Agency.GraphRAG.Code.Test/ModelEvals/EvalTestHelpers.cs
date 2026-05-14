using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Agency.GraphRAG.Code.Test.ModelEvals;

/// <summary>
/// Shared infrastructure helpers for model evaluation tests.
/// Provides repo discovery, configuration loading, environment-variable readers, and statistics utilities
/// used by both <c>QueryClassifierEvalTests</c> and <c>ClusterSummarizerEvalTests</c>.
/// </summary>
internal static class EvalTestHelpers
{
    internal const string CliAppSettingsRelativePath = @"src\GraphRAG.Code\Agency.GraphRAG.Code.Cli\appsettings.json";
    internal const string LlmClientSection = "LlmClient";
    internal const string SummarizerSection = "Summarizer";
    internal const int DefaultRunCount = 3;
    internal const int MaxRunCount = 20;

    /// <summary>
    /// Walks up the directory tree from <see cref="AppContext.BaseDirectory"/> until it finds
    /// the directory containing <c>src\Agency.slnx</c>, which is the repository root.
    /// </summary>
    internal static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "src", "Agency.slnx")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root containing src\\Agency.slnx.");
    }

    /// <summary>
    /// Loads the CLI's <c>appsettings.json</c> and overlays environment variables.
    /// </summary>
    internal static IConfigurationRoot LoadCliConfiguration()
    {
        string repoRoot = FindRepoRoot();
        string appSettingsPath = Path.Combine(repoRoot, CliAppSettingsRelativePath);
        if (!File.Exists(appSettingsPath))
        {
            throw new FileNotFoundException(
                $"CLI appsettings.json not found at expected location.",
                appSettingsPath);
        }

        return new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(appSettingsPath)!)
            .AddJsonFile(Path.GetFileName(appSettingsPath), optional: false)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// Returns the value for <paramref name="key"/> from <paramref name="configuration"/>,
    /// throwing when absent.
    /// </summary>
    internal static string RequireConfig(IConfiguration configuration, string key) =>
        configuration[key]
            ?? throw new InvalidOperationException($"Missing required configuration value '{key}' in CLI appsettings.json.");

    /// <summary>
    /// Reads a threshold from <paramref name="envVarName"/>, returning <paramref name="defaultValue"/>
    /// when the variable is absent, non-numeric, or outside [0, 1].
    /// </summary>
    internal static double ReadThreshold(string envVarName, double defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed is >= 0.0 and <= 1.0)
        {
            return parsed;
        }

        return defaultValue;
    }

    /// <summary>
    /// Reads the per-variant run count from <paramref name="envVarName"/>, returning
    /// <see cref="DefaultRunCount"/> when the variable is absent, non-integer, or out of [1, <see cref="MaxRunCount"/>].
    /// </summary>
    internal static int ReadRunCount(string envVarName)
    {
        string? raw = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 1 and <= MaxRunCount)
        {
            return parsed;
        }

        return DefaultRunCount;
    }

    /// <summary>
    /// Computes the sample standard deviation of <paramref name="values"/>.
    /// Returns <c>0.0</c> when the sequence has fewer than two elements.
    /// </summary>
    internal static double SampleStdDev(IEnumerable<double> values)
    {
        double[] sample = values.ToArray();
        if (sample.Length <= 1)
        {
            return 0.0;
        }

        double mean = sample.Average();
        double sumSquares = sample.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / (sample.Length - 1));
    }
}
