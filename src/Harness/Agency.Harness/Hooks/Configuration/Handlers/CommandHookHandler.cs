using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.Harness.Hooks.Configuration.Handlers;

internal sealed partial class CommandHookHandler : IHookHandler
{
    private readonly HookHandlerConfig _cfg;
    private readonly ILogger _logger;

    internal CommandHookHandler(HookHandlerConfig cfg, ILogger? logger = null)
    {
        _cfg = cfg;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _cfg.Command ?? throw new InvalidOperationException("Command is required."),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in _cfg.Args ?? [])
        {
            psi.ArgumentList.Add(arg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cfg.Timeout ?? 30));

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Start draining stdout/stderr before writing stdin to prevent pipe-buffer deadlock.
        var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = proc.StandardError.ReadToEndAsync(cts.Token);

        var payloadJson = JsonSerializer.Serialize(payload, HookPayload.SerializerOptions);
        try
        {
            await proc.StandardInput.WriteAsync(payloadJson);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // Process exited before reading stdin (e.g. "exit 1" scripts). Harmless — continue
            // to collect exit code and any output already written.
        }

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
#pragma warning disable S2486 // Generic exceptions should not be ignored
            try { proc.Kill(entireProcessTree: true); } catch { }
#pragma warning restore S2486 // Generic exceptions should not be ignored
            LogCommandTimedOut(_cfg.Command ?? string.Empty, _cfg.Timeout ?? 30);
            return new HookHandlerOutput(HookExitCodes.Timeout, null, null, null);
        }

        var stdout = await outTask;
        var stderr = await errTask;
        var json = TryParseLeadingJson(stdout);
        return new HookHandlerOutput(proc.ExitCode, json, stdout, stderr);
    }

    private static JsonElement? TryParseLeadingJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command hook '{Command}' timed out after {TimeoutSeconds}s and was killed.")]
    private partial void LogCommandTimedOut(string command, int timeoutSeconds);
}