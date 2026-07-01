using System.Text.Json;

namespace Agency.Harness.Tools;

public sealed class WriteFileTool : ITool
{
    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"" },
            ""content"": { ""type"": ""string"" }
        },
        ""required"": [""path"", ""content""]
    }").RootElement;

    public ToolDefinition Definition
    {
        get
        {
            return new ToolDefinition("write_file", "Writes content to a file at the specified path.", InputSchema);
        }
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        dynamic accessor = new JsonDynamicAccessor(input);
        string? path = accessor.path;
        string? content = accessor.content;

        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(new ToolResult("Path is required.", IsError: true));
        }

        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult(new ToolResult("Content is required.", IsError: true));
        }

        try
        {
            File.WriteAllText(path, content);
            return Task.FromResult(new ToolResult($"Wrote file: {path}", IsError: false));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error writing file: {ex.Message}", IsError: true));
        }
    }
}
