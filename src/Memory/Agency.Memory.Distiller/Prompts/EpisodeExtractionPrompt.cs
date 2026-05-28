using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Records;
using Agency.Agentic.Contexts;
using Microsoft.Extensions.AI;

namespace Agency.Memory.Distiller.Prompts;

/// <summary>
/// Renders the episode-extraction system prompt used by
/// <c>DistillerBackgroundService</c> on every <c>DistillationJob</c>.
/// </summary>
/// <remarks>
/// The template covers both Fact and Memory extraction in a single prompt.
/// The LLM decides which (or both, or neither) to emit per conversation excerpt.
/// Spec §18.1.
/// </remarks>
internal static class EpisodeExtractionPrompt
{
    /// <summary>Prompt template version. Bump when the template changes (Spec §18.5).</summary>
    internal const int Version = 1;

    /// <summary>
    /// Renders the full episode-extraction prompt string.
    /// </summary>
    /// <param name="job">The distillation job providing trigger metadata.</param>
    /// <param name="turns">The conversation turns in the extraction window.</param>
    /// <param name="focus">The current session focus context.</param>
    /// <param name="knownDomains">Existing domain labels for this user (for dedup hints).</param>
    /// <param name="recentFacts">Up to 10 recent fact records for dedup context.</param>
    /// <returns>The rendered prompt string ready to send to the LLM.</returns>
    internal static string Render(
        DistillationJob job,
        IReadOnlyList<ChatMessage> turns,
        FocusContext focus,
        IReadOnlyList<string> knownDomains,
        IReadOnlyList<Record> recentFacts)
    {
        string trigger = job.Trigger.ToString();
        string triggerSummaryLine = job.TriggerSummary is { Length: > 0 } s
            ? $"- **Trigger summary**: {s}"
            : string.Empty;

        string focusTitle = focus.Title ?? "(none)";
        string focusDomain = focus.Domain ?? "(none)";
        string focusTags = focus.Tags.Count > 0 ? string.Join(", ", focus.Tags) : "(none)";

        string knownDomainsCsv = knownDomains.Count > 0
            ? string.Join(", ", knownDomains)
            : "(none yet)";

        string recentFactsDump = recentFacts.Count > 0
            ? string.Join("\n", recentFacts.Select(static r => $"- \"{r.Title}\": {r.Value}"))
            : "(none)";

        string formattedTurns = FormatTurns(turns);

        return $@"SYSTEM:
You are a memory distiller for an AI agent. Your job is to read a conversation
excerpt and produce zero or more durable Records that capture what was learned
during the exchange. You operate AFTER the fact — the agent has already done its
work; you decide what is worth remembering.

## Record kinds

You may produce two kinds of Records:

1. **Fact** (`ContentType = ""Fact""`)
   - Static, impersonal, durable information.
   - Examples: user preferences, organisational rules, environment configurations,
     domain conventions, names, identifiers.
   - `Value` is a concise statement (1–3 sentences).
   - Example: ""User prefers Python for scripting tasks.""

2. **Memory** (`ContentType = ""Memory""`)
   - Episodic experience — a goal-bounded narrative following the
     Observation–Action–Outcome (OAO) pattern.
   - `Value` follows this Markdown template:

     ## Observation
     Describe the initial situation and the user's intent.

     ## Action
     Record the agent's reasoning, the tools called, and the decisions made.

     ## Outcome
     Assess the result. Did the goal succeed? Why or why not?

     ## Lesson
     A single-sentence takeaway for the agent's future self.

## Required fields per Record

- **`ContentType`** — `""Fact""` or `""Memory""`.
- **`Title`** (≤ 60 chars) — a concise label suitable for a system-prompt heading.
- **`Domain`** — group label; reuse one from ""Known Domains"" below if a match exists.
  Coin a new one only if no existing domain fits.
- **`Key`** — stable identifier within domain. Use a CONSISTENT form so that future
  Records about the same thing collide on `(Domain, Key)`.
  Examples: `LanguagePreference`, `SslDebugging_2026Q2`, `EnvVar.OPENAI_API_BASE`.
- **`Tags`** — 0..5 short tags (lowercase, hyphen-separated). Used for retrieval
  and for the dynamic vocabulary of the agent's `SetFocus` tool.
- **`Scope`** — `""Global""` or `""Session""`.
  - `""Global""` = available across all of this user's sessions (no session id).
  - `""Session""` = tagged with the current session id; remains visible from other
    sessions of the same user but is ranked lower there.
- **`Importance`** — 0.0..1.0; how valuable is this Record for future reference?
  Calibration anchors:
    1.0  Critical — agent MUST know this in future sessions (security rule, dietary restriction)
    0.7  Useful — likely relevant when similar work comes up (debugging conclusion)
    0.5  Worth keeping — may or may not surface again (preference detail)
    0.2  Marginal — borderline; include only if the user is likely to bring it up
    0.0  Don't write it.

## Quality bar

- **Do not duplicate** facts already known (see ""Recent Known Facts"" below). If a
  candidate fact is already captured, omit it.
- **Do not record trivia** (""user said hello"", ""agent acknowledged""). Memory is
  for what would be regrettable to lose, not for the conversation transcript.
- **Contradiction = overwrite.** If a fact contradicts an existing one (e.g., the
  user changed their preference), emit a new Fact with the SAME `(Domain, Key)`
  so it overwrites the old one on upsert.
- **Expansion = re-emit.** If you have a richer version of an existing Fact, emit
  the new richer one with the same `(Domain, Key)`.
- **If nothing is worth recording, return an empty `records` array.** It is
  better to skip a session than to pollute the store.

## Context

- **Trigger**: {trigger}
{triggerSummaryLine}
- **Session focus**: {focusTitle} / {focusDomain} / {focusTags}
- **Known domains** for this user: [{knownDomainsCsv}]
- **Recent known facts** (top 10 by recency, for dedup):
{recentFactsDump}

## Conversation excerpt

The following turns were exchanged between the user and the agent. Times are
relative to the session start.

{formattedTurns}

## Response format

Respond with strictly valid JSON. No prose, no markdown fences around the JSON.

{{
  ""records"": [
    {{
      ""ContentType"": ""Fact"" | ""Memory"",
      ""Title"": ""string (≤60 chars)"",
      ""Domain"": ""string"",
      ""Key"": ""string"",
      ""Tags"": [""string"", ...],
      ""Scope"": ""Global"" | ""Session"",
      ""Importance"": 0.0..1.0,
      ""Value"": ""markdown string""
    }},
    ...
  ]
}}";
    }

    /// <summary>
    /// Formats a list of chat messages as a readable transcript.
    /// </summary>
    /// <param name="turns">The messages to format.</param>
    /// <returns>A formatted string with role prefixes.</returns>
    private static string FormatTurns(IReadOnlyList<ChatMessage> turns)
    {
        if (turns.Count == 0)
        {
            return "(no turns in this window)";
        }

        return string.Join("\n\n", turns.Select(static m =>
        {
            string role = m.Role == ChatRole.User ? "User"
                : m.Role == ChatRole.Assistant ? "Assistant"
                : m.Role.Value;
            string text = string.Concat(m.Contents.OfType<TextContent>().Select(static t => t.Text));
            return $"**[{role}]**: {text}";
        }));
    }
}
