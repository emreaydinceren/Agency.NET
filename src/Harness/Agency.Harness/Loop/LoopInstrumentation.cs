using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Harness.Loop;

/// <summary>
/// Shared OpenTelemetry instruments for the Loop Kit (§10).
/// All counters, histograms, and the <see cref="ActivitySource"/> are static singletons
/// so they are created once per process — mirroring the <c>Agency.Harness.Agent</c> pattern.
/// </summary>
internal static class LoopInstrumentation
{
    /// <summary>The name of the <see cref="ActivitySource"/> used for Loop Kit distributed tracing.</summary>
    internal const string ActivitySourceName = "Agency.Harness.Loop";

    /// <summary>The name of the <see cref="Meter"/> used for Loop Kit metrics.</summary>
    internal const string MeterName = "Agency.Harness.Loop";

    /// <summary>Root activity source for all Loop Kit spans.</summary>
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter _meter = new(MeterName);

    // ── Counters ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Incremented once per loop run; tagged with <c>outcome</c> (e.g. <c>achieved</c>,
    /// <c>cap_reached</c>, <c>budget_exceeded</c>, <c>error</c>, <c>cancelled</c>).
    /// </summary>
    internal static readonly Counter<long> RunsCounter = _meter.CreateCounter<long>(
        "loop.runs",
        description: "Total number of loop runs, tagged by outcome");

    /// <summary>
    /// Incremented once per worker turn within a run.
    /// </summary>
    internal static readonly Counter<long> TurnsCounter = _meter.CreateCounter<long>(
        "loop.turns",
        description: "Total number of worker turns executed across all loop runs");

    /// <summary>
    /// Incremented once per Goalkeeper verdict; tagged with <c>verdict</c>
    /// (<c>continue</c> or <c>done</c>).
    /// </summary>
    internal static readonly Counter<long> VerdictsCounter = _meter.CreateCounter<long>(
        "loop.verdicts",
        description: "Total Goalkeeper verdicts, tagged by verdict (continue/done)");

    /// <summary>
    /// Token spend counter; tagged with <c>role</c> (<c>worker</c>/<c>goalkeeper</c>),
    /// <c>model</c>, <c>client_type</c>, and <c>token.type</c> (<c>input</c>/<c>output</c>).
    /// </summary>
    internal static readonly Counter<long> TokensCounter = _meter.CreateCounter<long>(
        "loop.tokens",
        description: "Tokens consumed by the loop, split by role (worker/goalkeeper), model, client_type, and token.type");

    // ── Histograms ────────────────────────────────────────────────────────────

    /// <summary>
    /// Wall-clock duration of a complete loop run in milliseconds; tagged with <c>outcome</c>.
    /// </summary>
    internal static readonly Histogram<double> RunDurationHistogram = _meter.CreateHistogram<double>(
        "loop.run.duration",
        unit: "ms",
        description: "Duration of a complete loop run in milliseconds, tagged by outcome");

    /// <summary>
    /// Wall-clock duration of a single worker turn (from <c>SendAsync</c> to verdict) in milliseconds.
    /// </summary>
    internal static readonly Histogram<double> TurnDurationHistogram = _meter.CreateHistogram<double>(
        "loop.turn.duration",
        unit: "ms",
        description: "Duration of a single worker turn (SendAsync through Goalkeeper evaluation) in milliseconds");
}
