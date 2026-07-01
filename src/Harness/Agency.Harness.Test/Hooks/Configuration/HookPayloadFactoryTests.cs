using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies that <see cref="HookPayloadFactory"/> produces correctly shaped
/// <see cref="HookPayload"/> instances with snake_case JSON keys and null-field omission.
/// </summary>
public sealed class HookPayloadFactoryTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonDocument Serialize(HookPayload p) =>
        JsonDocument.Parse(JsonSerializer.Serialize(p, HookPayload.SerializerOptions));

    private static Context MakeAgentContext(string sessionId = "test-session-id") =>
        new()
        {
            Query = new QueryContext { Prompt = "test" },
            Session = new SessionContext { Id = sessionId },
        };

    private static PreToolUseHookContext MakePreToolUseCtx(
        string toolName = "ReadFile",
        string? inputJson = null)
    {
        JsonElement input = inputJson is not null
            ? JsonSerializer.Deserialize<JsonElement>(inputJson)
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

        return new PreToolUseHookContext(toolName, input, MakeAgentContext());
    }

    private static PostToolUseHookContext MakePostToolUseCtx(bool isError = false) =>
        new PostToolUseHookContext(
            ToolName: "ReadFile",
            Input: JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
            Result: new ToolResult("some output", IsError: isError),
            AgentContext: MakeAgentContext());

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>A <c>PreToolUse</c> payload serializes with snake_case keys for session id, event name, tool name/input, cwd, iteration count, and total cost.</summary>
    [Fact]
    public void Payload_PreToolUse_HasSnakeCaseKeys()
    {
        PreToolUseHookContext ctx = MakePreToolUseCtx("ReadFile");

        HookPayload payload = HookPayloadFactory.ForPreToolUse(ctx);
        using JsonDocument doc = Serialize(payload);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("session_id", out JsonElement sessionId));
        Assert.False(string.IsNullOrEmpty(sessionId.GetString()));

        Assert.True(root.TryGetProperty("hook_event_name", out JsonElement eventName));
        Assert.Equal("PreToolUse", eventName.GetString());

        Assert.True(root.TryGetProperty("tool_name", out _));
        Assert.True(root.TryGetProperty("tool_input", out _));
        Assert.True(root.TryGetProperty("cwd", out _));
        Assert.True(root.TryGetProperty("iteration_count", out _));
        Assert.True(root.TryGetProperty("total_cost_usd", out _));
    }

    /// <summary>Fields that don't apply to a <c>PreToolUse</c> event (<c>prompt</c>, <c>message</c>, <c>tool_response</c>) are omitted from the serialized payload rather than emitted as <see langword="null"/>.</summary>
    [Fact]
    public void Payload_OmitsNullFields()
    {
        PreToolUseHookContext ctx = MakePreToolUseCtx("ReadFile");

        HookPayload payload = HookPayloadFactory.ForPreToolUse(ctx);
        using JsonDocument doc = Serialize(payload);
        JsonElement root = doc.RootElement;

        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.False(root.TryGetProperty("message", out _));
        Assert.False(root.TryGetProperty("tool_response", out _));
    }

    /// <summary>A <c>PostToolUse</c> payload's <c>tool_response</c> object carries an <c>is_error</c> flag reflecting the tool result's error state.</summary>
    [Fact]
    public void Payload_PostToolUse_ToolResponseIsErrorMapped()
    {
        PostToolUseHookContext ctx = MakePostToolUseCtx(isError: true);

        HookPayload payload = HookPayloadFactory.ForPostToolUse(ctx);
        using JsonDocument doc = Serialize(payload);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("tool_response", out JsonElement toolResponse));
        Assert.True(root.TryGetProperty("hook_event_name", out JsonElement eventName));
        Assert.Equal("PostToolUse", eventName.GetString());

        Assert.True(toolResponse.TryGetProperty("is_error", out JsonElement isError));
        Assert.True(isError.GetBoolean());
    }

    /// <summary>The tool input's original <see cref="JsonElement"/> shape and property values round-trip unchanged into the serialized <c>tool_input</c> field.</summary>
    [Fact]
    public void Payload_ToolInput_RoundTripsJsonElement()
    {
        PreToolUseHookContext ctx = MakePreToolUseCtx(
            toolName: "ReadFile",
            inputJson: """{"key":"value"}""");

        HookPayload payload = HookPayloadFactory.ForPreToolUse(ctx);
        using JsonDocument doc = Serialize(payload);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("tool_input", out JsonElement toolInput));
        Assert.True(toolInput.TryGetProperty("key", out JsonElement keyProp));
        Assert.Equal("value", keyProp.GetString());
    }

    /// <summary>A <c>UserPromptSubmit</c> payload includes the submitted prompt text and the correct event name.</summary>
    [Fact]
    public void Payload_UserPromptSubmit_CarriesPrompt()
    {
        UserPromptSubmitHookContext ctx = new UserPromptSubmitHookContext(
            Prompt: "hello",
            AgentContext: MakeAgentContext());

        HookPayload payload = HookPayloadFactory.ForUserPromptSubmit(ctx);
        using JsonDocument doc = Serialize(payload);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("hook_event_name", out JsonElement eventName));
        Assert.Equal("UserPromptSubmit", eventName.GetString());

        Assert.True(root.TryGetProperty("prompt", out JsonElement prompt));
        Assert.Equal("hello", prompt.GetString());
    }
}
