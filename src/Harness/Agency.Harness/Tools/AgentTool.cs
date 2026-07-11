
using System.Globalization;
using System.Text;
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;

namespace Agency.Harness.Tools;

/// <summary>
/// <see cref="ITool"/> that delegates a single, focused task to a specialized subagent (built via the
/// factory delegate supplied to the constructor) and returns its final text result. Because a subagent
/// runs headless, it cannot present interactive permission prompts: any tool call that would otherwise
/// park the subagent's turn awaiting permission is automatically denied so the subagent's run always
/// completes (spec §9.5).
/// </summary>
public sealed class AgentTool : ITool
{
    private const string SubAgentDenyMessage =
        "Sub-agents cannot request permission; grant a rule to the parent session instead.";

    private readonly Func<string?, string?, (AgentOptions, Agent, IToolRegistry)> agentFactory;

    /// <param name="agentFactory">
    /// Builds the subagent to run for a given client/model name, returning the subagent's
    /// <see cref="AgentOptions"/>, <see cref="Agent"/>, and <see cref="IToolRegistry"/>.
    /// </param>
    public AgentTool(Func<string?, string?, (AgentOptions, Agent, IToolRegistry)> agentFactory)
    {
        this.agentFactory = agentFactory;
    }

    static JsonElement InputSchema => JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""prompt"": { ""type"": ""string"",
                          ""description"": ""A single, narrow task the subagent must complete. Must not contain multiple unrelated tasks."" },
            ""clientName"": { ""type"": ""string"" ,
                         ""description"": ""The name of the client to use, leave empty to use the default client ."" },
            ""model"": { ""type"": ""string"",
                         ""description"": ""The name of the model to use, leave empty to use the default model."" }
        },
        ""required"": [""prompt""]
    }").RootElement;

    /// <summary>Gets the <c>subagent_tool</c> definition: JSON schema accepting a prompt and optional client/model overrides.</summary>
    public ToolDefinition Definition
    {
        get
        {
            return new ToolDefinition("subagent_tool", "Delegates a focused task to a " +
                "specialized subagent. The main agent uses this tool when a task should be " +
                "handled by a narrower, more specialized agent with its own constraints and " +
                "output format."
                , InputSchema);
        }
    }

    /// <summary>
    /// Runs the given <c>prompt</c> to completion in a subagent, auto-denying any permission
    /// requests raised during the run (subagents cannot interact with the user), and returns the
    /// subagent's final assistant text.
    /// </summary>
    /// <param name="input">JSON object with a required <c>prompt</c> field and optional <c>clientName</c>/<c>model</c> fields.</param>
    /// <param name="ct">Token used to cancel the subagent run.</param>
    /// <returns>
    /// A <see cref="ToolResult"/> carrying the subagent's final text; <see cref="ToolResult.IsError"/> is
    /// <see langword="true"/> unless the subagent finished with <see cref="AgentResultStatus.Success"/>.
    /// </returns>
    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        dynamic accessor = new JsonDynamicAccessor(input);

        string prompt = accessor.prompt;

        if (string.IsNullOrEmpty(prompt))
        {
            return new ToolResult("Prompt is required.", IsError: true);
        }

        string? clientName = accessor.clientName;

        string? model = accessor.model;

        var (agentOptions, agent, toolRegistry) = agentFactory(clientName, model);

        await using var chatSession = new ChatSession(agent, agentOptions, new ToolContext { Registry = toolRegistry });

        AgentResultStatus? status = null;

        var verboseSB = new StringBuilder();
        string finalText = string.Empty;

        // §9.5: collect pending requests; on AwaitingPermission auto-deny all and resume.
        // Loop over "current stream" — start with SendAsync, swap to ResumeWithPermissionsAsync on park.
        IAsyncEnumerable<AgentEvent> currentStream = chatSession.SendAsync(prompt, ct);

        while (true)
        {
            var pendingRequests = new List<PermissionRequestedEvent>();
            bool parked = false;

            await foreach (AgentEvent agentEvent in currentStream)
            {
                switch (agentEvent)
                {
                    case SessionStartedEvent ev:
                        verboseSB.AppendLine(CultureInfo.InvariantCulture, $"Session started: {ev.SessionId}");
                        break;
                    case ToolInvokedEvent ev:
                        verboseSB.AppendLine(CultureInfo.InvariantCulture, $"Tool invoked: {ev.ToolName} with input {ev.Input}");
                        break;
                    case IterationCompletedEvent ev:
                        verboseSB.AppendLine(CultureInfo.InvariantCulture, $"Iteration {ev.Iteration} completed.");
                        break;
                    case PermissionRequestedEvent ev:
                        pendingRequests.Add(ev);
                        break;
                    case AgentResultEvent ev:
                        verboseSB.AppendLine(CultureInfo.InvariantCulture, $"Agent result: {ev.Status} with {ev.FinalText}");
                        finalText = ev.FinalText ?? string.Empty;
                        status = ev.Status;
                        if (ev.Status == AgentResultStatus.AwaitingPermission)
                        {
                            parked = true;
                        }
                        break;
                }
            }

            if (!parked)
            {
                break;
            }

            // Auto-deny every pending request (§9.5) and resume.
            PermissionResponse[] responses = pendingRequests
                .Select(r => new PermissionResponse(r.RequestId, PermissionResponseKind.DenyOnce, SubAgentDenyMessage))
                .ToArray();

            currentStream = chatSession.ResumeWithPermissionsAsync(responses, ct);
        }

        return new ToolResult(finalText, IsError: (status ?? AgentResultStatus.Error) != AgentResultStatus.Success);
    }
}