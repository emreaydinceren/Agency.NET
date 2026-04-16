namespace Agency.Agentic.Tools;

using System.Text;
using System.Text.Json;
using Agentic.Contexts;

public class AgentTool : ITool
{
    private readonly Func<string?, string?, bool, (AgentOptions, Agent, IToolRegistry)> agentFactory;

    public AgentTool(Func<string?, string?, bool, (AgentOptions, Agent, IToolRegistry)> agentFactory)
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

        var (agentOptions, agent, toolRegistry) = agentFactory(clientName, model, false);

        var chatSession = new ChatSession(agent, agentOptions, new ToolContext { Registry = toolRegistry });

        AgentResultStatus? status = null;

        var verboseSB = new StringBuilder();
        string finalText = string.Empty;

        await foreach (AgentEvent agentEvent in chatSession.SendAsync(prompt, ct))
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
                case TextDeltaEvent ev:
                    verboseSB.AppendLine(ev.Delta);
                break;
                case ToolUseReceivedEvent ev:
                    verboseSB.AppendLine($"Tool use received: {ev.ToolName} with input {ev.ToolUseId}");
                break;
                case AgentResultEvent ev:
                    verboseSB.AppendLine($"Agent result: {ev.Status} with {ev.FinalText}");
                    finalText = ev.FinalText ?? string.Empty;
                    status = ev.Status;
                break;
            }

            string? text = agentEvent switch
            {
                SessionStartedEvent ev => $"Session started: {ev.SessionId}",
                ToolInvokedEvent ev => $"Tool invoked: {ev.ToolName} with input {ev.Input}",
                IterationCompletedEvent ev => $"Iteration {ev.Iteration} completed.",
                TextDeltaEvent ev => ev.Delta,
                ToolUseReceivedEvent ev => $"Tool use received: {ev.ToolName} with input {ev.ToolUseId}",
                AgentResultEvent ev => $"Agent result: {ev.Status} with {ev.FinalText}",
                _ => null
            };

            if (text is not null)
            {
                verboseSB.AppendLine(text);
            }
        }

        return new ToolResult(finalText, IsError: (status ?? AgentResultStatus.Error) != AgentResultStatus.Success);
    }
}