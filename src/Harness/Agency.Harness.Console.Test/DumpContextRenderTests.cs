using Agency.Harness.Console.Commands;
using Agency.Harness.Contexts;
using Agency.Llm.Common.Tools;
using Microsoft.Extensions.AI;
using Spectre.Console;
using System.Text;
using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="DumpContextCommand.Render"/> — the renderer behind
/// <c>/dump-context</c>. Each test injects a capturing <see cref="IChatOutput"/> seam, following
/// <see cref="LoopEventRenderTests"/>. Schema colouring is covered separately by
/// <see cref="DumpContextSchemaRenderTests"/>.
/// </summary>
public sealed class DumpContextRenderTests
{
    // ── Seam helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IChatOutput"/> that accumulates one markup document, mirroring
    /// <c>ConsoleOutput</c>'s escaping contract: the plain-text methods escape their argument,
    /// the markup methods pass it through. <see cref="TextWriterChatOutput"/> escapes nothing,
    /// so its buffer is not a valid markup document and cannot be parse-checked as a whole.
    /// </summary>
    private sealed class MarkupCapturingOutput : IChatOutput
    {
        private readonly StringBuilder _sb = new();

        public string Captured => this._sb.ToString();

        public void WriteLineMarkup(string text) => this._sb.AppendLine(text);

        public void WriteMarkup(string text) => this._sb.Append(text);

        public void WriteLine() => this.WriteLine(string.Empty);

        public void WriteLine(string text) => this.WriteLine(null, text);

        public void WriteLine(string? colorName, string text) => this.WriteLineMarkup(Wrap(colorName, text));

        public void Write(string text) => this.Write(null, text);

        public void Write(string? colorName, string text) => this.WriteMarkup(Wrap(colorName, text));

        public void WriteLineMarkdown(string text) => this.WriteLine(text);

        public void WriteMarkdownInBorderedPanel(string header, string text) => this.WriteLine(text);

        public void StartSpinner(string markup = "[yellow]Thinking...[/]") { }

        public void StopSpinner() { }

        private static string Wrap(string? colorName, string text) =>
            string.IsNullOrWhiteSpace(colorName)
                ? Spectre.Console.Markup.Escape(text)
                : $"[{colorName}]{Spectre.Console.Markup.Escape(text)}[/]";
    }

    private static ToolDefinition Tool(string name, string description = "Does a thing.", string schema = "{}") =>
        new(name, description, JsonDocument.Parse(schema).RootElement);

    /// <summary>
    /// Builds a snapshot, round-tripped through the codec so the tests see exactly the shape
    /// <c>/dump-context</c> receives — notably <c>FunctionResultContent.Result</c> as a
    /// <see cref="JsonElement"/> rather than the original CLR object.
    /// </summary>
    private static LlmRequestSnapshot MakeSnapshot(
        IReadOnlyList<ChatMessage>? messages = null,
        IReadOnlyList<ToolDefinition>? tools = null)
    {
        var snapshot = new LlmRequestSnapshot
        {
            CapturedAt = new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero),
            Iteration = 2,
            ModelId = "qwen3-coder",
            ClientType = "LM Studio",
            MaxOutputTokens = 8096,
            SystemPrompt = "You are a helpful assistant.\n## Memories\n- prefers dark mode",
            Messages = messages ?? [new ChatMessage(ChatRole.User, "Hello")],
            Tools = tools ?? [],
        };

        return LlmRequestSnapshotCodec.Deserialize(LlmRequestSnapshotCodec.Serialize(snapshot))!;
    }

    /// <summary>Renders a snapshot and returns the raw markup document.</summary>
    private static string Render(
        LlmRequestSnapshot snapshot,
        IReadOnlyDictionary<string, string>? serverByTool = null,
        IReadOnlyList<string>? serverOrder = null)
    {
        var output = new MarkupCapturingOutput();
        DumpContextCommand.Render(output, snapshot, serverByTool ?? new Dictionary<string, string>(), serverOrder ?? []);
        return output.Captured;
    }

    /// <summary>Renders a snapshot and returns the display text, with all markup stripped.</summary>
    private static string RenderText(
        LlmRequestSnapshot snapshot,
        IReadOnlyDictionary<string, string>? serverByTool = null,
        IReadOnlyList<string>? serverOrder = null) =>
        Markup.Remove(Render(snapshot, serverByTool, serverOrder));

    // ── Structure ─────────────────────────────────────────────────────────────

    /// <summary>All three section banners are emitted, with counts matching the snapshot.</summary>
    [Fact]
    public void Render_EmitsAllThreeSectionBanners()
    {
        string rendered = RenderText(MakeSnapshot(
            messages: [new ChatMessage(ChatRole.User, "Hi"), new ChatMessage(ChatRole.Assistant, "Hello")],
            tools: [Tool("read_file")]));

        Assert.Contains("══ SYSTEM PROMPT ══", rendered, StringComparison.Ordinal);
        Assert.Contains("══ MESSAGES (2) ══", rendered, StringComparison.Ordinal);
        Assert.Contains("══ TOOLS (1) ══", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header identifies which request this was — iteration, model, client, and token cap —
    /// so a dump taken mid-turn is not mistaken for the whole turn.
    /// </summary>
    [Fact]
    public void Render_HeaderCarriesIterationModelAndClient()
    {
        string rendered = RenderText(MakeSnapshot());

        Assert.Contains("iteration 2", rendered, StringComparison.Ordinal);
        Assert.Contains("qwen3-coder", rendered, StringComparison.Ordinal);
        Assert.Contains("LM Studio", rendered, StringComparison.Ordinal);
        Assert.Contains("8096", rendered, StringComparison.Ordinal);
    }

    /// <summary>The recorded system prompt is printed verbatim, not rebuilt.</summary>
    [Fact]
    public void Render_PrintsTheRecordedSystemPrompt()
    {
        string rendered = RenderText(MakeSnapshot());

        Assert.Contains("You are a helpful assistant.", rendered, StringComparison.Ordinal);
        Assert.Contains("- prefers dark mode", rendered, StringComparison.Ordinal);
    }

    // ── Message content branches ──────────────────────────────────────────────

    /// <summary>Text, tool-call, and tool-result content each render with their own label.</summary>
    [Fact]
    public void Render_RendersTextToolCallAndToolResultContent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "find the config"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", new Dictionary<string, object?> { ["q"] = "config" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "found it")]),
        };

        string rendered = RenderText(MakeSnapshot(messages: messages));

        Assert.Contains("find the config", rendered, StringComparison.Ordinal);
        Assert.Contains("tool-call", rendered, StringComparison.Ordinal);
        Assert.Contains("search", rendered, StringComparison.Ordinal);
        Assert.Contains("tool-result", rendered, StringComparison.Ordinal);
        Assert.Contains("call-1", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A string tool result renders unquoted. <see cref="FunctionResultContent.Result"/> is an
    /// untyped object, so it deserializes back as a <see cref="JsonElement"/>; without unwrapping
    /// it, every string result would be printed with its JSON quotes.
    /// </summary>
    [Fact]
    public void Render_StringToolResult_RendersWithoutJsonQuotes()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "found it")]),
        };

        string rendered = RenderText(MakeSnapshot(messages: messages));

        Assert.Contains("found it", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"found it\"", rendered, StringComparison.Ordinal);
    }

    // ── Tool grouping ─────────────────────────────────────────────────────────

    /// <summary>
    /// Tools bucket into the built-in set and one group per MCP server, built-in first. The
    /// snapshot carries no provenance, so the grouping is reconstructed from the pool's map.
    /// </summary>
    [Fact]
    public void Render_GroupsToolsByOriginWithBuiltInFirst()
    {
        var snapshot = MakeSnapshot(tools: [Tool("read_file"), Tool("memorize"), Tool("recall")]);
        var serverByTool = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memorize"] = "memory",
            ["recall"] = "memory",
        };

        string rendered = RenderText(snapshot, serverByTool, ["memory"]);

        Assert.Contains("Built-in (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("memory · MCP (2)", rendered, StringComparison.Ordinal);
        Assert.True(
            rendered.IndexOf("Built-in", StringComparison.Ordinal) < rendered.IndexOf("memory · MCP", StringComparison.Ordinal),
            "The built-in group must be rendered before any MCP server group.");
    }

    /// <summary>A real tool schema is rendered; the progressive-discovery placeholder is suppressed.</summary>
    [Fact]
    public void Render_RendersRealSchemasAndSuppressesThePlaceholder()
    {
        var snapshot = MakeSnapshot(tools:
        [
            Tool("deferred", schema: """{"type":"object"}"""),
            Tool("real", schema: """{"type":"object","properties":{"path":{"type":"string"}}}"""),
        ]);

        string rendered = RenderText(snapshot);

        Assert.Contains("path", rendered, StringComparison.Ordinal);
        Assert.Contains("deferred", rendered, StringComparison.Ordinal);
    }

    // ── Markup safety ─────────────────────────────────────────────────────────

    /// <summary>
    /// Everything emitted must be markup Spectre can parse, including message text and tool
    /// descriptions containing square brackets.
    /// </summary>
    [Fact]
    public void Render_ProducesMarkupSpectreCanParse()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "a [bracketed] message"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result with [brackets]")]),
        };
        var snapshot = MakeSnapshot(messages: messages, tools: [Tool("odd", description: "Takes a [value].")]);

        string rendered = Render(snapshot);

        // The decisive check: Spectre throws if any tag is unbalanced or a literal
        // '[' / ']' slipped through unescaped. Construction succeeding == valid markup.
        var ex = Record.Exception(() => new Markup(rendered));
        Assert.Null(ex);
    }
}
