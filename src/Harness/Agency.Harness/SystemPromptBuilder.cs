using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using System.Text;

namespace Agency.Harness;

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

        // Progressive tool discovery: schemas are withheld until requested (D-progressive-discovery).
        if (ctx.Tools.Registry is IProgressiveDiscovery)
        {
            sb.AppendLine();
            sb.AppendLine("Some tool parameter schemas are withheld to save context: a tool advertised with only a `{\"type\":\"object\"}` schema is a deferred tool. Always call tool_help(name) to retrieve its full parameter schema before invoking it.");
        }

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

        // Memory-retrieval records: Facts (from retrieval engine, Spec §6.4 / D.3).
        if (ctx.Knowledge.Records.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Facts");
            foreach (MemoryRecord record in ctx.Knowledge.Records)
            {
                sb.AppendLine($"- **{record.Title}** (Updated {Humanize(DateTimeOffset.UtcNow - record.UpdatedAt)})");
                sb.AppendLine($"  {record.Value}");
            }
        }

        // Memory-retrieval records: Episodic memories (from retrieval engine, Spec §6.4 / D.3).
        if (ctx.Memory.Records.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Memories");
            foreach (MemoryRecord record in ctx.Memory.Records)
            {
                sb.AppendLine($"- **{record.Title}** (Updated {Humanize(DateTimeOffset.UtcNow - record.UpdatedAt)})");
                sb.AppendLine($"  {record.Value}");
            }
        }

        // When both Record collections are empty, note it explicitly so the LLM knows
        // there are no retrieved memories (Spec §13 — "No relevant memories yet.").
        if (ctx.Knowledge.Records.Count == 0 && ctx.Memory.Records.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No relevant memories yet.");
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

        if (ctx.Environment.ContextWindowSize is { } windowSize)
        {
            long used = ctx.TotalUsage.InputTokens;
            if (used > 0)
            {
                sb.AppendLine($"Context window: {windowSize:N0} tokens (prior input: {used:N0}, est. remaining: {(windowSize - used):N0})");
            }
            else
            {
                sb.AppendLine($"Context window: {windowSize:N0} tokens");
            }
        }

        // User identity.
        if (ctx.User.Name is { } name)
        {
            sb.AppendLine($"User: {name}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a <see cref="TimeSpan"/> age into a human-readable relative string
    /// such as "just now", "3 minutes ago", "2 hours ago", "5 days ago", etc.
    /// </summary>
    /// <param name="age">The elapsed time since the record was last updated.</param>
    /// <returns>A human-readable recency string.</returns>
    internal static string Humanize(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            int minutes = (int)age.TotalMinutes;
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")} ago";
        }

        if (age.TotalHours < 24)
        {
            int hours = (int)age.TotalHours;
            return $"{hours} hour{(hours == 1 ? string.Empty : "s")} ago";
        }

        if (age.TotalDays < 7)
        {
            int days = (int)age.TotalDays;
            return $"{days} day{(days == 1 ? string.Empty : "s")} ago";
        }

        if (age.TotalDays < 30)
        {
            int weeks = (int)(age.TotalDays / 7);
            return $"{weeks} week{(weeks == 1 ? string.Empty : "s")} ago";
        }

        int months = (int)(age.TotalDays / 30);
        return $"{months} month{(months == 1 ? string.Empty : "s")} ago";
    }
}
