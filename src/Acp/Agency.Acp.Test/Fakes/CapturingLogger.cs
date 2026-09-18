using Microsoft.Extensions.Logging;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// A minimal <see cref="ILogger"/> test double that records every log entry's level, formatted
/// message, and exception, so a test can assert a fault was logged without depending on a specific
/// sink. Non-generic (unlike a typed <c>ILogger&lt;T&gt;</c> double) because <c>MethodDispatcher</c>
/// and <c>StdioTransport</c> both take a plain <see cref="ILogger"/>.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    /// <summary>Gets every entry logged through this instance, in order.</summary>
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => this.Entries.Add((logLevel, formatter(state, exception), exception));
}
