using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;

namespace Agency.Harness.Tools;

public class ExecutePowershellTool : ITool
{
    private static readonly string Description = $"Executes a PowerShell command and returns the output. OS: {Environment.OSVersion}, " +
        $"CurrentDirectory: {Environment.CurrentDirectory} , " +
        $"PathSeparator: {Path.PathSeparator}";

    private static readonly JsonElement InputSchema = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""command"": { ""type"": ""string"" }
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
            var command = accessor.command;

            if (string.IsNullOrEmpty(command))
            {
                return Task.FromResult(new ToolResult("Command is required.", IsError: true));
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
