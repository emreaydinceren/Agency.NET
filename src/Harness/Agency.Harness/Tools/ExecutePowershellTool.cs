using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;

namespace Agency.Harness.Tools;

public sealed class ExecutePowershellTool : ITool
{
    // The OS / working directory / path-separator advertised in the tool description are
    // machine-dependent, which makes the serialized tool definition (and therefore the LLM
    // request body) differ between machines — defeating cross-machine HTTP-cache replay
    // (dev records the cache; CI replays it in a Linux container). Each value falls back to
    // the live environment, so production and CI behaviour is byte-identical to before. When
    // regenerating the offline cache locally, set the *_OVERRIDE env vars to the values the
    // build machine will emit so the recorded blobs match the CI request bodies. CI itself
    // leaves the vars unset and uses its real environment.
    private static readonly string Description = BuildDescription();

    private static string BuildDescription()
    {
        string os = Environment.GetEnvironmentVariable("AGENCY_TOOL_OS_OVERRIDE")
            ?? Environment.OSVersion.ToString();
        string cwd = Environment.GetEnvironmentVariable("AGENCY_TOOL_CWD_OVERRIDE")
            ?? Environment.CurrentDirectory;
        string pathSeparator = Environment.GetEnvironmentVariable("AGENCY_TOOL_PATHSEP_OVERRIDE")
            ?? Path.PathSeparator.ToString();

        return $"Executes a PowerShell command and returns the output. OS: {os}, " +
            $"CurrentDirectory: {cwd} , " +
            $"PathSeparator: {pathSeparator}";
    }

    private static readonly JsonElement InputSchema = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""command"": { ""type"": ""string"", ""description"": ""The PowerShell command to execute, e.g. 'Get-Process'. This is a command line, not a file path."" }
        },
        ""required"": [""command""]
    }").RootElement.Clone();

    public ToolDefinition Definition 
    { 
        get
        {
              return new ToolDefinition("execute_powershell", Description, InputSchema);
        }
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        try
        {
            dynamic accessor = new JsonDynamicAccessor(input);
            string? command = accessor.command;

            // Postel's law: be liberal in what we accept. Weak models frequently put the command
            // under the wrong key (commonly 'path', borrowed from read_file/write_file) and then
            // fail to self-correct from an error. Since this tool has exactly one meaningful string
            // argument, a single string under any key is unambiguous — use it as the command.
            if (string.IsNullOrEmpty(command) && input.ValueKind == JsonValueKind.Object)
            {
                var stringArgs = input.EnumerateObject()
                    .Where(static p => p.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(p.Value.GetString()))
                    .ToList();
                if (stringArgs.Count == 1)
                {
                    command = stringArgs[0].Value.GetString();
                }
            }

            if (string.IsNullOrEmpty(command))
            {
                // Echo the keys we actually received so the model can self-correct. Reached only
                // when the input is empty or genuinely ambiguous (multiple string arguments).
                string received = input.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", input.EnumerateObject().Select(static p => $"'{p.Name}'"))
                    : "(none)";
                return Task.FromResult(new ToolResult(
                    $"Missing required 'command' parameter. Received: [{received}]. " +
                    "Pass the PowerShell command to run in the 'command' field.",
                    IsError: true));
            }

            using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();

            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddScript(command);

            var results = ps.Invoke();

            if (ps.HadErrors)
            {
                var errors = string.Join("\n", ps.Streams.Error.Select(static e => e.ToString()));
                return Task.FromResult(new ToolResult(errors, IsError: true));
            }

            if (results.Count == 0)
            {
                return Task.FromResult(new ToolResult("(no output)", IsError: false));
            }

            Runspace? previousDefaultRunspace = Runspace.DefaultRunspace;
            Runspace.DefaultRunspace = runspace;

            try
            {
                if (results.Count == 1)
                {
                    return Task.FromResult(new ToolResult(results[0].ToMarkdown(), IsError: false));
                }
                string markdown = results.ToMarkdownTable();
                return Task.FromResult(new ToolResult(markdown, IsError: false));
            }
            finally
            {
                Runspace.DefaultRunspace = previousDefaultRunspace;
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Exception executing command: {ex.Message}", IsError: true));
        }
    }
}
