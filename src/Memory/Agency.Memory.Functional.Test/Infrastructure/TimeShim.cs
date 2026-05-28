using Microsoft.Extensions.Time.Testing;

namespace Agency.Memory.Functional.Test.Infrastructure;

/// <summary>
/// Wraps <see cref="FakeTimeProvider"/> from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> to advance virtual time
/// without sleeping, enabling Group 4 (Hygiene) tests to trigger time-based
/// behaviour synchronously.
/// </summary>
/// <remarks>
/// <para>
/// The underlying <see cref="FakeTimeProvider"/> is exposed via
/// <see cref="Provider"/> so it can be injected wherever a
/// <see cref="TimeProvider"/> is accepted (e.g. the
/// <c>HygieneSweeperBackgroundService</c> and
/// <c>InactivityTimerService</c> constructors).
/// </para>
/// <para>
/// Use <see cref="AdvancePastTtl"/> to move virtual time past a TTL value
/// and trigger the sweep on demand. Use <see cref="Advance"/> when you need
/// fine-grained control over the amount of virtual time advanced.
/// </para>
/// <para>
/// <b>Usage example:</b>
/// <code>
/// var shim = new TimeShim();
/// // inject shim.Provider into the service under test
/// shim.AdvancePastTtl(TimeSpan.FromDays(30));
/// await sweeper.RunOnceAsync(ct);
/// </code>
/// </para>
/// <para>
/// <b>Note (TI-1 follow-up):</b>
/// Once <c>HygieneSweeperBackgroundService.RunOnceAsync</c> is accessible to
/// this project (via <c>InternalsVisibleTo</c> on <c>Agency.Memory.Hygiene</c>
/// or a public DI extension), Group 4 tests can inject <see cref="Provider"/>
/// into the sweeper and call <c>RunOnceAsync</c> directly to drive the sweep
/// without waiting for the periodic timer.
/// </para>
/// </remarks>
internal sealed class TimeShim
{
    /// <summary>
    /// Gets the <see cref="FakeTimeProvider"/> instance.
    /// Inject this wherever a <see cref="TimeProvider"/> is required.
    /// </summary>
    internal FakeTimeProvider Provider { get; } = new();

    /// <summary>
    /// Gets the virtual time currently reported by <see cref="Provider"/>.
    /// </summary>
    internal DateTimeOffset UtcNow => this.Provider.GetUtcNow();

    // ── Advance helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Advances virtual time by <paramref name="duration"/>.
    /// </summary>
    /// <param name="duration">The amount of virtual time to advance.</param>
    internal void Advance(TimeSpan duration) =>
        this.Provider.Advance(duration);

    /// <summary>
    /// Advances virtual time by <paramref name="ttl"/> plus one day.
    /// This guarantees that any record whose TTL is exactly <paramref name="ttl"/>
    /// will be considered expired by the hygiene sweeper.
    /// </summary>
    /// <param name="ttl">The TTL to advance past.</param>
    internal void AdvancePastTtl(TimeSpan ttl) =>
        this.Provider.Advance(ttl + TimeSpan.FromDays(1));

    /// <summary>
    /// Advances virtual time by <paramref name="staleAge"/> plus one day.
    /// Equivalent to <see cref="AdvancePastTtl"/> but named for the stale-age
    /// pruning context (importance pass in the hygiene sweeper).
    /// </summary>
    /// <param name="staleAge">The stale-age threshold to advance past.</param>
    internal void AdvancePastStaleAge(TimeSpan staleAge) =>
        this.Provider.Advance(staleAge + TimeSpan.FromDays(1));

    /// <summary>
    /// Advances virtual time by the inactivity timeout plus one second.
    /// Useful for triggering the <c>InactivityTimerService</c> in tests.
    /// </summary>
    /// <param name="inactivityTimeout">The configured inactivity timeout to advance past.</param>
    internal void AdvancePastInactivityTimeout(TimeSpan inactivityTimeout) =>
        this.Provider.Advance(inactivityTimeout + TimeSpan.FromSeconds(1));
}
