using Agency.Harness.Contexts;
using System.Globalization;
using System.Text;

namespace Agency.Harness.Memory;

/// <summary>
/// Pure function that renders everything memory-related — the operating policy, the recalled
/// facts, and the recalled episodic memories — as a <c>&lt;memory&gt;</c> block for injection
/// into a user message adjacent to the current turn.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately mirrors <see cref="Instructions.InstructionRenderer"/>: a tagged block in a
/// user message rather than a system-prompt section. Recall lived in the system prompt and was
/// reliably ignored — it sat behind the static preamble and, in the rendered prompt, behind the
/// tool catalogue too, leaving the records tens of thousands of characters from the question they
/// were supposed to answer. A user message next to the question is the closest a client can place
/// them without controlling the chat template.
/// </para>
/// <para>
/// Unlike the instructions block, which is appended to the conversation once at session start,
/// this is rebuilt and injected per request and never enters the persisted conversation. Recall is
/// the result of a vector search over the current message, so it changes every turn; persisting it
/// would accumulate stale blocks.
/// </para>
/// </remarks>
public static class MemoryRenderer
{
    /// <summary>The opening tag, also used to identify a rendered block.</summary>
    internal const string OpenTag = "<memory>";

    /// <summary>The closing tag.</summary>
    internal const string CloseTag = "</memory>";

    /// <summary>
    /// Renders the memory block for the current iteration. Never returns an empty string when
    /// memory is enabled: an explicit "nothing recalled" statement is itself information, and
    /// without it a model reads the absence as "I have no memory" (Spec §13).
    /// </summary>
    /// <param name="ctx">The current session context.</param>
    /// <returns>
    /// The formatted <c>&lt;memory&gt;</c> block, or an empty string when memory is disabled and
    /// nothing was recalled — in which case no message should be injected at all.
    /// </returns>
    public static string Build(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        bool hasRecords = ctx.Knowledge.Records.Count > 0 || ctx.Memory.Records.Count > 0;
        bool hasPolicy = ctx.Knowledge.MemoryPolicy is { Length: > 0 };

        // Nothing to say. Memory is off (the retrieval hook clears the policy) and nothing was
        // recalled, so injecting a block would only assert an absence the model never asked about.
        if (!hasRecords && !hasPolicy)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(OpenTag);

        if (ctx.Knowledge.MemoryPolicy is { Length: > 0 } policy)
        {
            sb.AppendLine("## Memory Policy");
            sb.AppendLine(policy);
            sb.AppendLine();
        }

        if (ctx.Knowledge.Records.Count > 0)
        {
            sb.AppendLine("## Facts from Memory");
            sb.AppendLine("Established facts about this user; apply them to the current request without asking for confirmation.");
            AppendRecords(sb, ctx.Knowledge.Records);
            sb.AppendLine();
        }

        // Episodic memories (from the retrieval engine, Spec §6.4 / D.3).
        if (ctx.Memory.Records.Count > 0)
        {
            sb.AppendLine("## Memories");
            AppendRecords(sb, ctx.Memory.Records);
            sb.AppendLine();
        }

        if (!hasRecords)
        {
            sb.AppendLine("No relevant memories yet.");
        }

        sb.AppendLine(CloseTag);
        return sb.ToString();
    }

    /// <summary>Renders one record per bullet with a human-readable recency hint.</summary>
    /// <param name="sb">The block being assembled.</param>
    /// <param name="records">The records to render.</param>
    private static void AppendRecords(StringBuilder sb, IReadOnlyList<MemoryRecord> records)
    {
        foreach (MemoryRecord record in records)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"- **{record.Title}** (Updated {SystemPromptBuilder.Humanize(DateTimeOffset.UtcNow - record.UpdatedAt)})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {record.Value}");
        }
    }
}
