using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.Acp.Transport;

/// <summary>
/// Moves newline-delimited JSON-RPC 2.0 frames between an input and an output stream.
///
/// A single reader loop reads lines from the input stream and hands each one to the
/// registered handler <em>without awaiting it</em> (see <see cref="RunAsync"/>) so that a slow or
/// blocked handler can never prevent the next line from being read. All writes funnel through one
/// <see cref="Channel{T}"/> drained by a single writer task, so lines emitted concurrently never
/// interleave.
/// </summary>
internal sealed partial class StdioTransport : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Channel<string> _writeChannel;
    private readonly Task _writerTask;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a transport bound to the given input and output streams. The transport does not
    /// take ownership of disposing <paramref name="input"/> or <paramref name="output"/>.
    /// </summary>
    /// <param name="input">The stream to read newline-delimited frames from.</param>
    /// <param name="output">The stream to write newline-delimited frames to.</param>
    /// <param name="logger">
    /// Optional logger for the reader loop's last-resort fault handling (see
    /// <see cref="InvokeHandlerAsync"/>). Defaults to <see cref="NullLogger"/> so existing call
    /// sites that never pass one still compile unchanged.
    /// </param>
    public StdioTransport(Stream input, Stream output, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false, NewLine = "\n" };
        _writeChannel = Channel.CreateUnbounded<string>();
        _writerTask = RunWriterLoopAsync();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Enqueues <paramref name="line"/> to be written as a single newline-terminated frame.
    /// Safe to call concurrently from multiple callers; ordering across the writer task is
    /// serialized so frames never interleave.
    /// </summary>
    public async ValueTask SendAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        await _writeChannel.Writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads newline-delimited frames from the input stream until end-of-stream or cancellation,
    /// invoking <paramref name="onLine"/> for each one <em>without awaiting its returned task</em>
    /// so a blocking handler cannot stall the reader loop (required so <c>session/cancel</c> can
    /// overtake an in-flight <c>session/prompt</c>).
    /// </summary>
    public async Task RunAsync(Func<string, CancellationToken, Task> onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            // Deliberately not awaited: the reader must keep consuming stdin while a handler
            // (e.g. an in-flight session/prompt) is still running.
            _ = InvokeHandlerAsync(onLine, line, cancellationToken);
        }
    }

    private async Task InvokeHandlerAsync(Func<string, CancellationToken, Task> onLine, string line, CancellationToken cancellationToken)
    {
        try
        {
            await onLine(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last resort only: MethodDispatcher.DispatchAsync (spec §14.3, P6) already maps every
            // handler fault to a JSON-RPC error response, so onLine above should never throw. If it
            // does anyway, the fault is in the dispatcher itself, not in a method handler — this
            // catch exists solely so that fault cannot take down the reader loop or the process; it
            // is not a place any handler is expected to route errors through.
            LogHandlerFaultAtLastResort(_logger, ex);
        }
    }

    /// <summary>Logs a fault caught by <see cref="InvokeHandlerAsync"/>'s last-resort catch — see its remarks for what that means.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in the stdio reader loop's last-resort handler.")]
    private static partial void LogHandlerFaultAtLastResort(ILogger logger, Exception ex);

    private async Task RunWriterLoopAsync()
    {
        await foreach (string line in _writeChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _writer.WriteLineAsync(line).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes the write channel, waits for the writer task to drain, and releases the
    /// underlying reader/writer.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _writeChannel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _reader.Dispose();
    }
}
