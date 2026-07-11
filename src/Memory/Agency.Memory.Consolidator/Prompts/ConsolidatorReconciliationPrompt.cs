using System.Globalization;
using System.Text;
using Agency.Memory.Common.Records;

namespace Agency.Memory.Consolidator.Prompts;

/// <summary>
/// Builds the system prompt given to the consolidator sub-agent (Spec §18.2).
/// Renders all records for the user in a format the LLM can reason over.
/// </summary>
internal static class ConsolidatorReconciliationPrompt
{
    /// <summary>The current prompt version. Bump when the template changes (Spec §18.5).</summary>
    /// <remarks>v3: added same-Domain/Key merge priority rule to prevent LLM skipping obvious near-duplicates.</remarks>
    internal const int Version = 3;

    /// <summary>
    /// Renders the full reconciliation prompt for the given user and record set.
    /// </summary>
    /// <param name="userId">The user whose records are being consolidated.</param>
    /// <param name="records">All records for the user.</param>
    /// <param name="maxIterations">The iteration cap communicated to the sub-agent.</param>
    /// <param name="factThreshold">Embedding similarity threshold hint for Facts.</param>
    /// <param name="memoryThreshold">Embedding similarity threshold hint for Memories.</param>
    /// <returns>The full system prompt string.</returns>
    internal static string Render(
        string userId,
        IReadOnlyList<Record> records,
        int maxIterations,
        double factThreshold,
        double memoryThreshold)
    {
        var sb = new StringBuilder();

        sb.AppendLine("SYSTEM:");
        sb.AppendLine("You are a memory consolidator. You operate over the long-term memory store of an");
        sb.AppendLine("AI agent for a specific user. Your job is to reconcile the existing Records:");
        sb.AppendLine("merge near-duplicates, update outdated facts, delete redundant entries — leaving");
        sb.AppendLine("the store more accurate, more concise, and easier to retrieve from.");
        sb.AppendLine();
        sb.AppendLine("## Tools available");
        sb.AppendLine();
        sb.AppendLine("- `Memory_Merge(recordIds: string[], newRecord: Record)`");
        sb.AppendLine("  Atomically delete the listed Records and insert a new combined Record.");
        sb.AppendLine("  Use when two or more Records describe the same thing with overlapping content.");
        sb.AppendLine();
        sb.AppendLine("- `Memory_Update(recordId: string, newValue?: string, newImportance?: number)`");
        sb.AppendLine("  Update a single Record's Value and/or Importance in place.");
        sb.AppendLine("  Use when an existing Record is partially outdated or its importance was misjudged.");
        sb.AppendLine();
        sb.AppendLine("- `Memory_Delete(recordId: string)`");
        sb.AppendLine("  Hard-delete a Record.");
        sb.AppendLine("  Use when a Record is obsolete, contradicted and superseded, or simply trivial.");
        sb.AppendLine();
        sb.AppendLine("- `Memory_Done()`");
        sb.AppendLine("  Signal you are finished. ALWAYS call this last. The pass ends as soon as you do.");
        sb.AppendLine();
        sb.AppendLine("## Decision categories");
        sb.AppendLine();
        sb.AppendLine("For each cluster of related Records, decide one of:");
        sb.AppendLine();
        sb.AppendLine("- **MERGE** — multiple Records cover the same fact/episode with overlap. Produce");
        sb.AppendLine("  one comprehensive Record. Preserve the most informative content from each.");
        sb.AppendLine("  Choose the higher Importance of the merged Records.");
        sb.AppendLine();
        sb.AppendLine("- **UPDATE — contradiction** — a newer Record contradicts an older one and the");
        sb.AppendLine("  user's current state matches the newer. Overwrite with the newer content.");
        sb.AppendLine();
        sb.AppendLine("- **UPDATE — expansion** — a newer Record adds detail to an older sparse one.");
        sb.AppendLine("  Expand the existing Record to be more exhaustive.");
        sb.AppendLine();
        sb.AppendLine("- **DELETE** — Record is trivial, contradicted-and-superseded, or no longer");
        sb.AppendLine("  relevant. Use sparingly; deletion is irreversible.");
        sb.AppendLine("  **Structural rule (overrides the conservative default):** when a Record");
        sb.AppendLine("  has Importance < 0.1 AND Age > 30 days AND its own Value describes it as");
        sb.AppendLine("  obsolete, superseded, or no-longer-relevant, DELETE it by default. These");
        sb.AppendLine("  clear-cut cases are not judgement calls — do not leave them in place.");
        sb.AppendLine();
        sb.AppendLine("- **SKIP** — Record is fine as-is. Take no action.");
        sb.AppendLine();
        sb.AppendLine("## Guidelines");
        sb.AppendLine();
        sb.AppendLine("- **Same Domain + Key = strong merge signal.** When two Records share the exact");
        sb.AppendLine("  same Domain AND Key they describe the same fact slot. If their Values overlap");
        sb.AppendLine("  in meaning, MERGE them — do not leave duplicates in the same slot.");
        sb.AppendLine("  The merged Value should preserve the most specific detail from either record.");
        sb.AppendLine("- **Be conservative.** When in doubt, leave a Record alone. False merges lose");
        sb.AppendLine("  information; false deletes lose information; false updates corrupt");
        sb.AppendLine("  information. The cost of inaction is small.");
        sb.AppendLine("- **Do not invent.** Only synthesise from content that is actually in the");
        sb.AppendLine("  existing Records. Do not add facts you \"think\" should be true.");
        sb.AppendLine("- **Preserve information density.** A merge should never lose anything");
        sb.AppendLine("  important. If you can't merge without loss, leave both Records alone.");
        sb.AppendLine("- **High-Importance Records have a stronger prior** of being correct. Be more");
        sb.AppendLine("  cautious about overwriting or deleting them. Bias toward MERGE-with-preserve");
        sb.AppendLine("  over DELETE.");
        sb.AppendLine("- **Tied contradictions stay.** If two Records contradict and neither is clearly");
        sb.AppendLine("  more recent or higher-Importance, keep both. The agent can resolve at");
        sb.AppendLine("  retrieval time.");
        sb.AppendLine("- **Don't touch records you don't understand.** If a Record's purpose is unclear,");
        sb.AppendLine("  SKIP it.");
        sb.AppendLine();
        sb.AppendLine("## Stop condition");
        sb.AppendLine();
        sb.AppendLine($"Call `Memory_Done()` when there is nothing left to consolidate. You should not");
        sb.AppendLine(CultureInfo.InvariantCulture, $"normally need more than {maxIterations} iterations. The system will force");
        sb.AppendLine("termination if you exceed this.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Existing Records for user `{userId}`");
        sb.AppendLine();

        if (records.Count == 0)
        {
            sb.AppendLine("(No records. Call Memory_Done immediately.)");
        }
        else
        {
            foreach (var r in records)
            {
                RenderRecord(sb, r);
                sb.AppendLine();
            }
        }

        sb.AppendLine("(Each Record is shown with: id, ContentType, Domain, Key, Title, Tags, Importance,");
        sb.AppendLine("Age (e.g., \"3 days ago\"), and a truncated Value preview. Full Values can be");
        sb.AppendLine("inspected via the records themselves — they're presented above.)");
        sb.AppendLine();
        sb.AppendLine("## Similarity threshold hints (informational)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Fact:   {factThreshold:F2}    — Records with embedding similarity above this are usually about the same subject.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Memory: {memoryThreshold:F2}");
        sb.AppendLine();
        sb.AppendLine("These are HINTS, not rules. Apply your own judgment. A high-similarity pair may");
        sb.AppendLine("still be intentionally separate (e.g., progress notes on the same project at");
        sb.AppendLine("different milestones).");

        return sb.ToString();
    }

    /// <summary>
    /// Renders a single record in the format described by Spec §18.2 implementation notes.
    /// </summary>
    private static void RenderRecord(StringBuilder sb, Record r)
    {
        string age = HumanizeAge(r.Age);
        string valuePreview = r.Value.Length > 200
            ? r.Value[..200] + "…"
            : r.Value;
        string tagsCsv = r.Tags.Count > 0 ? string.Join(", ", r.Tags) : "(none)";

        sb.AppendLine(CultureInfo.InvariantCulture, $"### [{r.ContentType}] \"{r.Title}\" (id: {r.Id})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Domain/Key**: {r.Domain} / {r.Key}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Tags**: {tagsCsv}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Importance**: {r.Importance:F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Age**: {age}");
        sb.AppendLine();
        sb.AppendLine(valuePreview);
    }

    /// <summary>
    /// Converts a <see cref="TimeSpan"/> into a human-readable relative-time string.
    /// </summary>
    private static string HumanizeAge(TimeSpan age)
    {
        double totalMinutes = age.TotalMinutes;

        if (totalMinutes < 1)
        {
            return "just now";
        }

        if (totalMinutes < 60)
        {
            int m = (int)totalMinutes;
            return $"{m} minute{(m == 1 ? "" : "s")} ago";
        }

        double totalHours = age.TotalHours;
        if (totalHours < 24)
        {
            int h = (int)totalHours;
            return $"{h} hour{(h == 1 ? "" : "s")} ago";
        }

        double totalDays = age.TotalDays;
        if (totalDays < 7)
        {
            int d = (int)totalDays;
            return $"{d} day{(d == 1 ? "" : "s")} ago";
        }

        if (totalDays < 30)
        {
            int w = (int)(totalDays / 7);
            return $"{w} week{(w == 1 ? "" : "s")} ago";
        }

        int mo = (int)(totalDays / 30);
        return $"{mo} month{(mo == 1 ? "" : "s")} ago";
    }
}
