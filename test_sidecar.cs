using System.Text.Json;
using System.Diagnostics;
using System.Text;

var source = await File.ReadAllTextAsync(@"src/GraphRAG.Code/Agency.GraphRAG.Code.Postgres/PostgresGraphStore.cs");
var request = JsonSerializer.Serialize(new {
    id = "test",
    file = "PostgresGraphStore.cs",
    language = "csharp",
    source = source
}, new JsonSerializerOptions(JsonSerializerDefaults.Web));

var proc = new Process {
    StartInfo = new ProcessStartInfo("node", @"tools/treesitter-sidecar/index.js") {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }
};

proc.Start();
await proc.StandardInput.WriteLineAsync(request);
await proc.StandardInput.FlushAsync();

string? response = await proc.StandardOutput.ReadLineAsync();
proc.WaitForExit();
Console.WriteLine($"Exit code: {proc.ExitCode}");
if (response != null) {
    Console.WriteLine($"Response length: {response.Length}");
    Console.WriteLine($"First 100 chars: {response.Substring(0, Math.Min(100, response.Length))}");
}
