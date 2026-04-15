using System.Text.Json;

namespace Agency.Agentic.Tools;

public class ReadFileTool : ITool
{
    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"" }
        },
        ""required"": [""path""]
    }").RootElement;

    public ToolDefinition Definition { 
        get
        {
              return new ToolDefinition("read_file", "Reads the contents of a file at the specified path.", InputSchema);
        }
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        var path = input.GetProperty("path").GetString();
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(new ToolResult("Path is required.", IsError: true));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult(new ToolResult($"File not found: {path}", IsError: true));
        }

        try
        {
            var content = File.ReadAllText(path);
            return Task.FromResult(new ToolResult(content, IsError: false));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error reading file: {ex.Message}", IsError: true));
        }
    }
}

public class AgentTool : ITool
{
    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""prompt"": { ""type"": ""string"" },
            ""model"": { ""type"": ""string"" }
        },
        ""required"": [""prompt"", ""model""]
    }").RootElement;

    public ToolDefinition Definition
    {
        get
        {
            return new ToolDefinition("agent_tool", "Delegates to the agent's main loop, allowing the agent to decide the next action to take. " +
                "Input should be a JSON object with a single 'reasoning' property describing why delegation is needed."
                , InputSchema);
        }
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}