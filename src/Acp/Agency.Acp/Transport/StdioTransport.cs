using System.Text;
using System.Threading.Channels;

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
internal sealed class StdioTransport : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Channel<string> _writeChannel;
    private readonly Task _writerTask;

    /// <summary>
    /// Creates a transport bound to the given input and output streams. The transport does not
    /// take ownership of disposing <paramref name="input"/> or <paramref name="output"/>.
    /// </summary>
    public StdioTransport(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false, NewLine = "\n" };
        _writeChannel = Channel.CreateUnbounded<string>();
        _writerTask = RunWriterLoopAsync();
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

    private static async Task InvokeHandlerAsync(Func<string, CancellationToken, Task> onLine, string line, CancellationToken cancellationToken)
    {
        try
        {
            await onLine(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A handler fault must not take down the reader loop or the process; the handler is
            // responsible for translating its own failures into JSON-RPC error responses.
        }
    }

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
