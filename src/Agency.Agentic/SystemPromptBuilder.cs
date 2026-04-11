using System.Text;

namespace Agency.Agentic;

/// <summary>
/// Pure function that assembles the system prompt from the current <see cref="Context"/>.
/// Called once per loop iteration so that <see cref="KnowledgeContext"/> is always fresh (D3).
/// Being a pure function makes it trivially unit-testable without running the agent loop.
/// </summary>
public static class SystemPromptBuilder
{
    /// <summary>Builds the complete system prompt string for one loop iteration.</summary>
    /// <param name="ctx">The current session context.</param>
    /// <returns>The assembled system prompt.</returns>
    public static string Build(Context ctx)
    {
        var sb = new StringBuilder();

        // Stable identity / persona.
        sb.AppendLine("You are an autonomous agent operating inside the Agency runtime.");
        sb.AppendLine();

        // ReAct reasoning instruction (D11) — chain-of-thought before tool use.
        sb.AppendLine("When solving a task, always explain your reasoning before taking actions.");
        sb.AppendLine("Break complex problems into steps: Reason about what to do, Act using tools, then Observe the results before deciding next steps.");

        // KnowledgeContext re-injected every iteration (D3).
        if (ctx.Knowledge.Facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Knowledge");
            foreach (string fact in ctx.Knowledge.Facts)
            {
                sb.AppendLine($"- {fact}");
            }
        }

        // Long-term memory summarized into the system prompt.
        if (ctx.Memory.LongTermMemory.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Long-term memory");
            foreach (string item in ctx.Memory.LongTermMemory)
            {
                sb.AppendLine($"- {item}");
            }
        }

        // Temporal grounding.
        if (ctx.Temporal.CurrentDateUtc is { } date)
        {
            sb.AppendLine();
            sb.AppendLine($"Current date/time (UTC): {date:yyyy-MM-dd HH:mm:ss}");
        }

        // Environmental context.
        if (ctx.Environment.OperatingSystem is { } os)
        {
            sb.AppendLine($"Operating system: {os}");
        }

        // User identity.
        if (ctx.User.Name is { } name)
        {
            sb.AppendLine($"User: {name}");
        }

        return sb.ToString();
    }
}
