using System.Text.Json;

namespace Agency.Harness.Tools;

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
        dynamic accessor = new JsonDynamicAccessor(input);
        string? path = accessor.path;
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
