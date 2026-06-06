
using System.Text;
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;

namespace Agency.Harness.Tools;
public class AgentTool : ITool
{
    private const string SubAgentDenyMessage =
        "Sub-agents cannot request permission; grant a rule to the parent session instead.";

    private readonly Func<string?, string?, (AgentOptions, Agent, IToolRegistry)> agentFactory;

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
                        verboseSB.AppendLine($"Session started: {ev.SessionId}");
                        break;
                    case ToolInvokedEvent ev:
                        verboseSB.AppendLine($"Tool invoked: {ev.ToolName} with input {ev.Input}");
                        break;
                    case IterationCompletedEvent ev:
                        verboseSB.AppendLine($"Iteration {ev.Iteration} completed.");
                        break;
                    case PermissionRequestedEvent ev:
                        pendingRequests.Add(ev);
                        break;
                    case AgentResultEvent ev:
                        verboseSB.AppendLine($"Agent result: {ev.Status} with {ev.FinalText}");
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