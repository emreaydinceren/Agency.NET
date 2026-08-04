using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Regression coverage for the busy-spin bug in <see cref="ConsolePicker.Show{T}"/>: the render loop used to
/// call <c>console.Input.ReadKey(intercept: true)</c> directly, and — because Spectre's
/// <see cref="IAnsiConsoleInput.ReadKey"/> is non-blocking by contract, returning <see langword="null"/>
/// immediately when no key is buffered — a pass with no key ready fell straight through to <c>continue</c>
/// with no throttle, spinning the CPU and repainting the picker as fast as possible. The fix polls
/// <see cref="IAnsiConsoleInput.IsKeyAvailable"/> and sleeps between checks, mirroring the discipline already
/// used by <see cref="ConsoleInputReader.ReadLineAsync"/>.
/// </summary>
[Collection("AnsiConsoleTests")]
public sealed class ConsolePickerTests
{
    // ---------------------------------------------------------------------------
    // Minimal test doubles
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Wraps a real <see cref="TestConsole"/> for everything except <see cref="Input"/> — <see cref="TestConsole.Input"/>
    /// is a fixed, get-only <see cref="TestConsoleInput"/> that cannot be swapped for a custom fake, so this
    /// decorator substitutes the input surface while delegating rendering (cursor, profile, write pipeline) to
    /// the real double.
    /// </summary>
    private sealed class InputSwappedConsole(IAnsiConsole inner, IAnsiConsoleInput input) : IAnsiConsole
    {
        public Profile Profile => inner.Profile;

        public IAnsiConsoleCursor Cursor => inner.Cursor;

        public IAnsiConsoleInput Input => input;

        public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;

        public RenderPipeline Pipeline => inner.Pipeline;

        public void Clear(bool home) => inner.Clear(home);

        public void Write(IRenderable renderable) => inner.Write(renderable);

        public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);
    }

    /// <summary>
    /// A fake <see cref="IAnsiConsoleInput"/> that reports no key available for <paramref name="nullResponsesBeforeKey"/>
    /// polls before finally offering <paramref name="finalKey"/>. Both <see cref="IsKeyAvailable"/> and
    /// <see cref="ReadKey"/> drive the same underlying counter, so the fake behaves correctly whether the caller
    /// polls availability first (the fixed, correct discipline) or calls <see cref="ReadKey"/> directly in a
    /// tight loop (the old, buggy discipline) — either path eventually converges on the same key.
    /// </summary>
    private sealed class ThrottleTrackingInput(int nullResponsesBeforeKey, ConsoleKeyInfo finalKey) : IAnsiConsoleInput
    {
        private int _probeCount;

        public int IsKeyAvailableCallCount { get; private set; }

        public int ReadKeyCallCount { get; private set; }

        public List<DateTime> PollTimestamps { get; } = [];

        public bool IsKeyAvailable()
        {
            this.IsKeyAvailableCallCount++;
            this.PollTimestamps.Add(DateTime.UtcNow);
            this._probeCount++;
            return this._probeCount > nullResponsesBeforeKey;
        }

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            this.ReadKeyCallCount++;
            this._probeCount++;
            return this._probeCount > nullResponsesBeforeKey ? finalKey : null;
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult(this.ReadKey(intercept));
    }

    /// <summary>A fake <see cref="IAnsiConsoleInput"/> backed by a plain queue of keys, all available immediately.</summary>
    private sealed class QueuedInput(params ConsoleKeyInfo[] keys) : IAnsiConsoleInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public bool IsKeyAvailable() => this._keys.Count > 0;

        public ConsoleKeyInfo? ReadKey(bool intercept) => this._keys.Count > 0 ? this._keys.Dequeue() : null;

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
            Task.FromResult(this.ReadKey(intercept));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0') => new(ch, key, false, false, false);

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    /// <summary>Reproduces the busy-spin bug: the picker must throttle between polls rather than hammering the input source.</summary>
    [Fact]
    public void Show_polls_and_sleeps_instead_of_busy_spinning_while_no_key_is_available()
    {
        // Five polls report no key before the sixth finally offers Enter. A busy-spin regression would
        // either bypass IsKeyAvailable entirely (old code called ReadKey directly) or call it thousands of
        // times with no delay; the fixed code calls it a small, bounded number of times with a real sleep
        // between each.
        const int nullResponsesBeforeKey = 5;
        var input = new ThrottleTrackingInput(nullResponsesBeforeKey, Key(ConsoleKey.Enter));

        using var testConsole = new TestConsole();
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = new InputSwappedConsole(testConsole, input);
        try
        {
            var items = new List<ConsolePickerItem<string>>
            {
                new("a", "Option A", "a"),
            };

            DateTime start = DateTime.UtcNow;
            string? result = ConsolePicker.Show(items, cancelValue: null, cancellationToken: TestContext.Current.CancellationToken);
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.Equal("a", result);

            // Proves polling happened (not a straight ReadKey spin) and that each poll actually waited —
            // a busy spin would finish in well under a millisecond.
            Assert.True(input.IsKeyAvailableCallCount > 0, "Expected the picker to poll IsKeyAvailable().");
            Assert.True(
                elapsed >= TimeSpan.FromMilliseconds(150),
                $"Expected the picker to throttle between polls (>=150ms for {nullResponsesBeforeKey} null responses), but it returned after {elapsed.TotalMilliseconds}ms.");

            // Bounded: a regression back to a tight spin would drive this into the tens of thousands.
            Assert.True(
                input.IsKeyAvailableCallCount <= 50,
                $"Expected a small, bounded number of polls, got {input.IsKeyAvailableCallCount}.");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    /// <summary>Regression coverage: a key that's already available must resolve without waiting out a poll delay.</summary>
    [Fact]
    public void Show_resolves_promptly_once_a_key_is_available()
    {
        // Regression coverage for normal behavior: a key that is immediately available should resolve
        // without waiting out any polling delay.
        var input = new QueuedInput(Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter));

        using var testConsole = new TestConsole();
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = new InputSwappedConsole(testConsole, input);
        try
        {
            var items = new List<ConsolePickerItem<string>>
            {
                new("a", "Option A", "a"),
                new("b", "Option B", "b"),
            };

            DateTime start = DateTime.UtcNow;
            string? result = ConsolePicker.Show(items, cancelValue: null, cancellationToken: TestContext.Current.CancellationToken);
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.Equal("b", result);
            Assert.True(
                elapsed < TimeSpan.FromMilliseconds(500),
                $"Expected an immediately-available key to resolve promptly, took {elapsed.TotalMilliseconds}ms.");
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    /// <summary>An already-cancelled token must break the poll loop rather than hang or spin forever.</summary>
    [Fact]
    public void Show_returns_cancel_value_when_the_token_is_already_cancelled()
    {
        // The picker never sees a key; cancellation must break the poll loop rather than hang forever.
        var input = new ThrottleTrackingInput(nullResponsesBeforeKey: int.MaxValue, Key(ConsoleKey.Enter));

        using var testConsole = new TestConsole();
        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = new InputSwappedConsole(testConsole, input);
        try
        {
            var items = new List<ConsolePickerItem<string>>
            {
                new("a", "Option A", "a"),
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            string? result = ConsolePicker.Show(items, cancelValue: "cancelled", cancellationToken: cts.Token);

            Assert.Equal("cancelled", result);
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}
