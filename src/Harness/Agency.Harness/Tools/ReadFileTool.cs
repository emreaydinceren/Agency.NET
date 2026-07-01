using System.Text.Json;

namespace Agency.Harness.Tools;

/// <summary><see cref="ITool"/> that reads and returns the full text contents of a file at a given path.</summary>
public sealed class ReadFileTool : ITool
{
    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"" }
        },
        ""required"": [""path""]
    }").RootElement;

    /// <summary>Gets the <c>read_file</c> definition: JSON schema accepting a single required <c>path</c> string.</summary>
    public ToolDefinition Definition {
        get
        {
              return new ToolDefinition("read_file", "Reads the contents of a file at the specified path.", InputSchema);
        }
    }

    /// <summary>Reads the file at <c>path</c> and returns its contents as text.</summary>
    /// <param name="input">JSON object expected to contain a <c>path</c> string field.</param>
    /// <param name="ct">Cancellation token; not currently observed since file I/O is synchronous.</param>
    /// <returns>
    /// A <see cref="ToolResult"/> with the file's contents; <see cref="ToolResult.IsError"/> is
    /// <see langword="true"/> if <c>path</c> is missing, the file does not exist, or reading fails.
    /// </returns>
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
