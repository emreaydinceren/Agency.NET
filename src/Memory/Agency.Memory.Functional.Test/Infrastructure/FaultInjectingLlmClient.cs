using Agency.Memory.Distiller.Services;
using System.Net;

namespace Agency.Memory.Functional.Test.Infrastructure;

/// <summary>
/// Marker interface used by Group 5 tests to distinguish the
/// <see cref="FaultInjectingLlmClient"/> decorator from the real
/// <see cref="ILlmClientAdapter"/> in DI registrations.
/// </summary>
/// <remarks>
/// Register <see cref="FaultInjectingLlmClient"/> as both
/// <c>ILlmClientAdapter</c> and <see cref="IFaultInjectingLlmClient"/> so
/// that Group 5 tests can resolve it by the marker to configure faults,
/// while the Distiller resolves it via the normal <c>ILlmClientAdapter</c> interface.
/// </remarks>
internal interface IFaultInjectingLlmClient : ILlmClientAdapter
{
    /// <summary>
    /// Configures the decorator to simulate an HTTP 429 (Too Many Requests)
    /// response for the next <paramref name="callCount"/> calls.
    /// </summary>
    /// <param name="callCount">Number of calls to intercept with 429.</param>
    void Inject429Transient(int callCount);

    /// <summary>
    /// Configures the decorator to simulate an HTTP 400 (Bad Request)
    /// response for the next single call.
    /// </summary>
    void Inject400Permanent();

    /// <summary>
    /// Configures the decorator to return <paramref name="malformedJson"/>
    /// on the next call, then defer to the real client for subsequent calls.
    /// </summary>
    /// <param name="malformedJson">
    /// The malformed JSON string to return once.
    /// Defaults to an obviously broken fragment when <see langword="null"/>.
    /// </param>
    void InjectMalformedJsonOnce(string? malformedJson = null);

    /// <summary>
    /// Configures the decorator to throw an <see cref="HttpRequestException"/>
    /// on the next call, simulating a network/embedding-service outage.
    /// </summary>
    void InjectNetworkErrorOnce();

    /// <summary>Gets the total number of calls made to <see cref="ILlmClientAdapter.SendAsync"/>.</summary>
    int TotalCallCount { get; }

    /// <summary>Gets the number of calls that were intercepted and faulted.</summary>
    int InterceptedCallCount { get; }
}

/// <summary>
/// An <see cref="ILlmClientAdapter"/> decorator that wraps the real client and
/// intercepts distiller/consolidator LLM calls according to a configurable
/// fault schedule.
/// </summary>
/// <remarks>
/// <para>
/// Faults are applied sequentially: each <see cref="SendAsync"/> call
/// dequeues the next pending fault (if any) and applies it. The real client
/// is called on all non-faulted turns.
/// </para>
/// <para>
/// Fault injection methods are not thread-safe with respect to each other;
/// configure faults before starting the host or before the background service
/// processes the next job.
/// </para>
/// </remarks>
internal sealed class FaultInjectingLlmClient : IFaultInjectingLlmClient
{
    private readonly ILlmClientAdapter _inner;
    private readonly Queue<PendingFault> _faultQueue = new();
    private int _totalCallCount;
    private int _interceptedCallCount;

    /// <summary>
    /// Initialises a new <see cref="FaultInjectingLlmClient"/> wrapping
    /// <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The real <see cref="ILlmClientAdapter"/> to delegate to.</param>
    internal FaultInjectingLlmClient(ILlmClientAdapter inner)
    {
        this._inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public int TotalCallCount => this._totalCallCount;

    /// <inheritdoc/>
    public int InterceptedCallCount => this._interceptedCallCount;

    // ── Fault configuration ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Inject429Transient(int callCount)
    {
        for (int i = 0; i < callCount; i++)
        {
            this._faultQueue.Enqueue(new PendingFault(FaultKind.Http429));
        }
    }

    /// <inheritdoc/>
    public void Inject400Permanent()
    {
        this._faultQueue.Enqueue(new PendingFault(FaultKind.Http400));
    }

    /// <inheritdoc/>
    public void InjectMalformedJsonOnce(string? malformedJson = null)
    {
        this._faultQueue.Enqueue(new PendingFault(
            FaultKind.MalformedJson,
            Payload: malformedJson ?? """{"records": [{INVALID"""));
    }

    /// <inheritdoc/>
    public void InjectNetworkErrorOnce()
    {
        this._faultQueue.Enqueue(new PendingFault(FaultKind.NetworkError));
    }

    // ── ILlmClientAdapter ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="prompt"/> to the LLM, applying the next queued fault
    /// if one is pending, or delegating to the real client otherwise.
    /// </summary>
    /// <param name="prompt">The full prompt string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM response string, or a faulted response as configured.</returns>
    public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
    {
        System.Threading.Interlocked.Increment(ref this._totalCallCount);

        if (this._faultQueue.TryDequeue(out PendingFault? fault) && fault is not null)
        {
            System.Threading.Interlocked.Increment(ref this._interceptedCallCount);
            return ApplyFault(fault);
        }

        return await this._inner.SendAsync(prompt, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies the pending fault, either throwing an exception or returning a
    /// crafted response string.
    /// </summary>
    /// <param name="fault">The fault descriptor to apply.</param>
    /// <returns>The faulted response string when the fault produces a return value.</returns>
    /// <exception cref="HttpRequestException">
    /// Thrown for <see cref="FaultKind.Http429"/>, <see cref="FaultKind.Http400"/>,
    /// and <see cref="FaultKind.NetworkError"/> faults.
    /// </exception>
    private static string ApplyFault(PendingFault fault)
    {
        switch (fault.Kind)
        {
            case FaultKind.Http429:
                throw new HttpRequestException(
                    "Simulated 429 Too Many Requests.",
                    inner: null,
                    statusCode: HttpStatusCode.TooManyRequests);

            case FaultKind.Http400:
                throw new HttpRequestException(
                    "Simulated 400 Bad Request (permanent).",
                    inner: null,
                    statusCode: HttpStatusCode.BadRequest);

            case FaultKind.NetworkError:
                throw new HttpRequestException(
                    "Simulated network error (embedding/LLM service unreachable).");

            case FaultKind.MalformedJson:
                return fault.Payload
                    ?? throw new InvalidOperationException(
                        "MalformedJson fault has no payload.");

            default:
                throw new InvalidOperationException($"Unknown fault kind: {fault.Kind}.");
        }
    }

    // ── Nested types ──────────────────────────────────────────────────────────

    /// <summary>Discriminated union describing a pending fault to apply on the next call.</summary>
    private enum FaultKind
    {
        /// <summary>Throw HTTP 429 (transient).</summary>
        Http429,

        /// <summary>Throw HTTP 400 (permanent).</summary>
        Http400,

        /// <summary>Return a pre-configured malformed JSON string.</summary>
        MalformedJson,

        /// <summary>Throw a network-level <see cref="HttpRequestException"/>.</summary>
        NetworkError,
    }

    /// <summary>A queued fault descriptor.</summary>
    /// <param name="Kind">The fault kind to apply.</param>
    /// <param name="Payload">
    /// Optional payload (used for <see cref="FaultKind.MalformedJson"/>).
    /// </param>
    private sealed record PendingFault(FaultKind Kind, string? Payload = null);
}
