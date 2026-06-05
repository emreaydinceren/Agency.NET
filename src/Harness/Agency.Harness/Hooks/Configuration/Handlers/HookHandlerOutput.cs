namespace Agency.Harness.Hooks.Configuration.Handlers;

internal sealed record HookHandlerOutput(
    int ExitCode,
    System.Text.Json.JsonElement? Json,
    string? RawStdout,
    string? RawStderr);

internal static class HookExitCodes
{
    internal const int Ok = 0;
    internal const int BlockingDeny = 2;
    internal const int NonBlockingError = 1;
    internal const int Timeout = -1;
}