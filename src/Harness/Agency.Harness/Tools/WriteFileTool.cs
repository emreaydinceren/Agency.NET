using System.Text.Json;

namespace Agency.Harness.Tools;

/// <summary><see cref="ITool"/> that writes text content to a file at a given path, creating or overwriting it.</summary>
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

    /// <summary>Gets the <c>write_file</c> definition: JSON schema accepting required <c>path</c> and <c>content</c> strings.</summary>
    public ToolDefinition Definition
    {
        get
        {
            return new ToolDefinition("write_file", "Writes content to a file at the specified path.", InputSchema);
        }
    }

    /// <summary>Writes <c>content</c> to the file at <c>path</c>, creating it if it does not exist or overwriting it if it does.</summary>
    /// <param name="input">JSON object expected to contain <c>path</c> and <c>content</c> string fields.</param>
    /// <param name="ct">Cancellation token; not currently observed since file I/O is synchronous.</param>
    /// <returns>
    /// A <see cref="ToolResult"/> confirming the write; <see cref="ToolResult.IsError"/> is
    /// <see langword="true"/> if <c>path</c> or <c>content</c> is missing, or if the write fails.
    /// </returns>
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
