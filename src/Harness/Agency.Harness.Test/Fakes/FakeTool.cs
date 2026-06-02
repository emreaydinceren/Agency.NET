using System.Text.Json;

namespace Agency.Harness.Test.Fakes;

/// <summary>
/// A configurable test double for <see cref="ITool"/> that records invocations
/// and returns a predetermined <see cref="ToolResult"/>.
/// </summary>
internal sealed class FakeTool : ITool
{
    private readonly Func<JsonElement, ToolResult> _handler;

    /// <param name="name">The tool name registered in the registry.</param>
    /// <param name="handler">Optional custom handler; defaults to returning a plain text result.</param>
    public FakeTool(string name, Func<JsonElement, ToolResult>? handler = null)
    {
        this.Definition = new ToolDefinition(
            name,
            $"Fake tool: {name}",
            JsonDocument.Parse("{}").RootElement);

        _handler = handler ?? (_ => new ToolResult($"Result from {name}"));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition { get; }

    /// <summary>Gets the number of times this tool was invoked.</summary>
    public int InvokeCount { get; private set; }

    /// <summary>Gets the input arguments received on each invocation, in order.</summary>
    public List<JsonElement> ReceivedInputs { get; } = [];

    /// <inheritdoc/>
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        this.InvokeCount++;
        this.ReceivedInputs.Add(input);
        return Task.FromResult(_handler(input));
    }
}
