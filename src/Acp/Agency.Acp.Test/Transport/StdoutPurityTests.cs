using System.Text;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Agency.Acp.Test.Transport;

/// <summary>
/// Spec E-4 (high severity): a stray write anywhere in the process must never corrupt the
/// JSON-RPC stream on stdout. These tests boot the real composition root (<see cref="Program"/>)
/// with logging at Trace against redirected in-memory streams and verify both halves of the
/// guarantee: stdout carries only well-formed JSON-RPC lines, and logging never targets the console.
/// </summary>
public sealed class StdoutPurityTests
{
    /// <summary>Every line written to stdout while driving initialize plus a failing method parses as JSON-RPC, and the configured logger writes to a file, never to Console.Out.</summary>
    [Fact]
    public async Task RunAsync_InitializeThenFailingMethod_StdoutCarriesOnlyJsonRpcAndLoggingNeverTargetsConsole()
    {
        string logFilePath = Path.Combine(Path.GetTempPath(), $"agency-acp-purity-{Guid.NewGuid():N}.log");
        var recordingConsoleOut = new StringWriter();
        TextWriter originalConsoleOut = Console.Out;
        Console.SetOut(recordingConsoleOut);

        try
        {
            Program.ConfigureLogging(logFilePath);

            string initializeRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";
            string failingRequest = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/load\",\"params\":{}}";
            string stdin = initializeRequest + "\n" + failingRequest + "\n";

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
            using var output = new MemoryStream();

            await Program.RunAsync(input, output, TestContext.Current.CancellationToken);
            string[] lines = await WaitForLinesAsync(output, expectedCount: 2, TestContext.Current.CancellationToken);

            Assert.Equal(2, lines.Length);
            foreach (string line in lines)
            {
                JObject.Parse(line); // throws if not well-formed JSON
            }

            JObject initializeResponse = JObject.Parse(lines[0]);
            Assert.NotNull(initializeResponse["result"]);
            JObject failingResponse = JObject.Parse(lines[1]);
            Assert.Equal(-32601, ((JObject)failingResponse["error"]!)["code"]!.Value<int>());

            // The recording writer stands in for the real Console.Out: if any log sink (or a stray
            // write anywhere in the dispatch path) had targeted the console, it would show up here.
            Assert.Equal(string.Empty, recordingConsoleOut.ToString());

            await Log.CloseAndFlushAsync(); // release the file handle before reading it back
            string logContents = await File.ReadAllTextAsync(logFilePath, TestContext.Current.CancellationToken);
            Assert.Contains("Agency.Acp stdio loop starting.", logContents, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalConsoleOut);
            await Log.CloseAndFlushAsync();
            if (File.Exists(logFilePath))
            {
                File.Delete(logFilePath);
            }
        }
    }

    private static async Task<string[]> WaitForLinesAsync(MemoryStream output, int expectedCount, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string[] lines = [];
        while (DateTime.UtcNow < deadline)
        {
            lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= expectedCount)
            {
                break;
            }

            await Task.Delay(10, cancellationToken);
        }

        return lines;
    }
}
