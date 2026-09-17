using System.Text;
using Agency.Acp.Transport;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Transport;

/// <summary>
/// Behavioral tests for <see cref="StdioTransport"/>: framing on the write side, and reader-loop
/// concurrency guarantees on the read side.
/// </summary>
public sealed class StdioTransportTests
{
    /// <summary>A single enqueued message is written as exactly one newline-terminated, parseable JSON line.</summary>
    [Fact]
    public async Task SendAsync_SingleNotification_WritesSingleNewlineTerminatedParseableLine()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await using var transport = new StdioTransport(input, output);

        await transport.SendAsync("""{"jsonrpc":"2.0","method":"session/update"}""", TestContext.Current.CancellationToken);
        await transport.DisposeAsync();

        string written = Encoding.UTF8.GetString(output.ToArray());
        Assert.EndsWith("\n", written);
        Assert.Equal(1, written.Count(c => c == '\n'));
        string line = written.TrimEnd('\n');
        JObject parsed = JObject.Parse(line);
        Assert.Equal("session/update", parsed["method"]!.Value<string>());
    }

    /// <summary>100 notifications emitted concurrently from 10 tasks each produce a well-formed line; none interleave.</summary>
    [Fact]
    public async Task SendAsync_ConcurrentFromManyTasks_ProducesOnlyWellFormedLines()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var transport = new StdioTransport(input, output);

        const int taskCount = 10;
        const int perTask = 10;
        var tasks = Enumerable.Range(0, taskCount).Select(taskIndex => Task.Run(async () =>
        {
            for (int i = 0; i < perTask; i++)
            {
                string line = "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"task\":" + taskIndex + ",\"i\":" + i + "}}";
                await transport.SendAsync(line, TestContext.Current.CancellationToken);
            }
        }));
        await Task.WhenAll(tasks);
        await transport.DisposeAsync();

        string written = Encoding.UTF8.GetString(output.ToArray());
        string[] lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(taskCount * perTask, lines.Length);
        foreach (string line in lines)
        {
            JObject parsed = JObject.Parse(line);
            Assert.Equal("session/update", parsed["method"]!.Value<string>());
        }
    }

    /// <summary>A request read from stdin surfaces to the handler with its id and method intact.</summary>
    [Fact]
    public async Task RunAsync_RequestOnStdin_SurfacesIdAndMethodIntact()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":42,"method":"initialize","params":{}}""" + "\n"));
        using var output = new MemoryStream();
        await using var transport = new StdioTransport(input, output);

        JObject? received = null;
        var handled = new TaskCompletionSource();
        await transport.RunAsync((line, _) =>
        {
            received = JObject.Parse(line);
            handled.TrySetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(received);
        Assert.Equal(42, received!["id"]!.Value<int>());
        Assert.Equal("initialize", received["method"]!.Value<string>());
    }

    /// <summary>The reader loop does not await the handler: a blocking handler on one line does not stop the next line from being read.</summary>
    [Fact]
    public async Task RunAsync_HandlerBlocks_DoesNotStallReaderLoopForNextLine()
    {
        string frame1 = """{"jsonrpc":"2.0","id":1,"method":"first"}""";
        string frame2 = """{"jsonrpc":"2.0","id":2,"method":"second"}""";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frame1 + "\n" + frame2 + "\n"));
        using var output = new MemoryStream();
        await using var transport = new StdioTransport(input, output);

        var blockFirstHandler = new TaskCompletionSource();
        var secondLineSeen = new TaskCompletionSource();

        await transport.RunAsync(async (line, _) =>
        {
            var obj = JObject.Parse(line);
            string method = obj["method"]!.Value<string>()!;
            if (method == "first")
            {
                await blockFirstHandler.Task; // never completes during this test
            }
            else
            {
                secondLineSeen.TrySetResult();
            }
        }, TestContext.Current.CancellationToken);

        await secondLineSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(secondLineSeen.Task.IsCompletedSuccessfully);
    }
}
