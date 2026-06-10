namespace Agency.Harness.Console;

/// <summary>
/// A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> always returns a fixed instant.
/// Registered only when the host runs under <c>DOTNET_ENVIRONMENT=Test</c> so the agent's
/// "Current date/time (UTC)" system-prompt line is byte-stable across runs — both locally
/// (cache record) and in CI (cache replay) — making console agent turns HTTP-cache-replayable.
/// Production registers no <see cref="TimeProvider"/>, so the live clock is used as before.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}
