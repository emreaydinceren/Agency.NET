using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using System.Globalization;
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
        ArgumentNullException.ThrowIfNull(ctx);

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

        // Skills catalog: only model-invocable skills are listed (DisableModelInvocation == false).
        List<Skills.Skill> modelInvocableSkills = ctx.Skills.List()
            .Where(s => !s.DisableModelInvocation)
            .ToList();

        if (modelInvocableSkills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Skills");
            foreach (Skills.Skill skill in modelInvocableSkills)
            {
                string entry = string.IsNullOrEmpty(skill.WhenToUse)
                    ? $"- **{skill.Name}** — {skill.Description}"
                    : $"- **{skill.Name}** — {skill.Description} ({skill.WhenToUse})";
                sb.AppendLine(entry);
            }
            sb.AppendLine("To use a skill, call the `skill` tool with its name.");
        }

        // KnowledgeContext re-injected every iteration (D3).
        if (ctx.Knowledge.Facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Knowledge");
            foreach (string fact in ctx.Knowledge.Facts)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {fact}");
            }
        }

        // Long-term memory summarized into the system prompt.
        if (ctx.Memory.LongTermMemory.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Long-term memory");
            foreach (string item in ctx.Memory.LongTermMemory)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
            }
        }

        // Memory-retrieval records: Facts (from retrieval engine, Spec §6.4 / D.3).
        // Both Record collections are populated only by the retrieval engine, and MemoryLastRetrievedAt
        // is stamped on every completed pass — including a zero-hit one — so together they separate
        // "memory is attached but still empty" from "no memory at all". That distinction is what keeps
        // the paragraph below honest: it only claims continuity where continuity actually exists.
        // Without it a cold start renders as nothing but "No relevant memories yet.", which reads as
        // amnesia and invites the model to disclaim memory it does in fact have.
        // MemoryEnabled is checked first because /memory can switch memory off mid-session: the Context
        // outlives the turn, so MemoryLastRetrievedAt stays stamped and Records keep the prior turn's
        // hits even once retrieval has stopped running. Without this the paragraph would keep promising
        // continuity on a session the user just opted out of.
        bool hasRecalledRecords = ctx.Knowledge.Records.Count > 0 || ctx.Memory.Records.Count > 0;
        bool memoryAttached = ctx.MemoryEnabled
            && (hasRecalledRecords || ctx.MemoryLastRetrievedAt is not null);

        if (memoryAttached)
        {
            sb.AppendLine();
            sb.AppendLine("## Memory");
            sb.AppendLine("You are not stateless. Memory of earlier sessions with this user persists and is recalled for you automatically.");
            sb.AppendLine(hasRecalledRecords
                ? "Everything under Facts and Memories below is your own recollection: treat it as already known, use it without asking the user to restate it, and never claim you cannot remember previous conversations."
                : "Nothing has been recalled for this user yet — never take that as evidence that you are unable to remember across conversations.");
            sb.AppendLine("What matters from this session is captured for you in the background, so the user never has to tell you to remember something.");
        }

        if (ctx.Knowledge.Records.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Facts");
            foreach (MemoryRecord record in ctx.Knowledge.Records)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{record.Title}** (Updated {Humanize(DateTimeOffset.UtcNow - record.UpdatedAt)})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {record.Value}");
            }
        }

        // Memory-retrieval records: Episodic memories (from retrieval engine, Spec §6.4 / D.3).
        if (ctx.Memory.Records.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Memories");
            foreach (MemoryRecord record in ctx.Memory.Records)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{record.Title}** (Updated {Humanize(DateTimeOffset.UtcNow - record.UpdatedAt)})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {record.Value}");
            }
        }

        // When both Record collections are empty, note it explicitly so the LLM knows
        // there are no retrieved memories (Spec §13 — "No relevant memories yet.").
        if (!hasRecalledRecords)
        {
            sb.AppendLine();
            sb.AppendLine("No relevant memories yet.");
        }

        // Temporal grounding.
        if (ctx.Temporal.CurrentDateUtc is { } date)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Current date/time (UTC): {date:yyyy-MM-dd HH:mm:ss}");
        }

        // Environmental context.
        if (ctx.Environment.OperatingSystem is { } os)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Operating system: {os}");
        }

        if (ctx.Environment.ContextWindowSize is { } windowSize)
        {
            long used = ctx.TotalUsage.InputTokens;
            if (used > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Context window: {windowSize:N0} tokens (prior input: {used:N0}, est. remaining: {(windowSize - used):N0})");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Context window: {windowSize:N0} tokens");
            }
        }

        // User identity.
        if (ctx.User.Name is { } name)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"User: {name}");
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
