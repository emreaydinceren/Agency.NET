using System.Text.Json;
using Agency.Memory.Common.Records;

namespace Agency.Memory.Distiller.Prompts;

/// <summary>
/// Parses the JSON response from the episode-extraction LLM call into a list of
/// <see cref="Record"/> instances (Spec §8.2).
/// </summary>
/// <remarks>
/// The parser tolerates code fences (<c>```json</c> / <c>```</c>) around the JSON body.
/// Unknown fields are ignored. Required fields per Record:
/// <c>ContentType</c>, <c>Title</c>, <c>Domain</c>, <c>Key</c>, <c>Importance</c>, <c>Value</c>.
/// Throws <see cref="ExtractionParseException"/> for any structural failure.
/// </remarks>
internal static class EpisodeExtractionParser
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses <paramref name="llmResponse"/> into zero or more <see cref="Record"/> instances.
    /// </summary>
    /// <param name="llmResponse">The raw LLM response string, possibly with code fences.</param>
    /// <param name="userId">The user ID to stamp on each parsed record.</param>
    /// <param name="sessionId">The session ID for Session-scoped records; <see langword="null"/> for Global.</param>
    /// <returns>A list of parsed, partially-filled records. <c>Embedding</c> is empty; <c>Id</c> is a new GUID.</returns>
    /// <exception cref="ExtractionParseException">Thrown when the response is not parseable JSON or missing required fields.</exception>
    internal static IReadOnlyList<Record> Parse(string llmResponse, string userId, string? sessionId)
    {
        string cleaned = StripCodeFences(llmResponse.Trim());

        // Smaller models routinely wrap the object in conversation ("Here is the JSON:" before,
        // "Let me know if you need more." after) despite the prompt demanding JSON only. Demanding
        // that the whole body parse turns that into a dead-lettered distillation, so take the first
        // balanced object instead. A response with no object at all still falls through to the
        // JsonException path below and reports the original text's failure.
        if (TryExtractJsonObject(cleaned, out string extracted))
        {
            cleaned = extracted;
        }

        ExtractionRoot root;
        try
        {
            root = JsonSerializer.Deserialize<ExtractionRoot>(cleaned, _jsonOptions)
                ?? throw new ExtractionParseException("Deserialization returned null.");
        }
        catch (JsonException ex)
        {
            throw new ExtractionParseException($"Failed to parse LLM response as JSON: {ex.Message}", ex);
        }

        if (root.Records is null)
        {
            throw new ExtractionParseException("JSON response missing 'records' array.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var result = new List<Record>(root.Records.Count);

        foreach (ExtractionRecord r in root.Records)
        {
            ValidateRequiredFields(r);

            ContentType contentType = ParseContentType(r.ContentType!);
            double importance = r.Importance ?? 0.5;

            // Scope determines whether session id is stamped.
            string? recordSessionId = string.Equals(r.Scope, "Session", StringComparison.OrdinalIgnoreCase)
                ? sessionId
                : null;

            result.Add(Record.Create(
                id: Guid.NewGuid().ToString(),
                userId: userId,
                sessionId: recordSessionId,
                contentType: contentType,
                domain: r.Domain!,
                key: r.Key!,
                title: r.Title!,
                value: r.Value!,
                tags: r.Tags ?? [],
                importance: importance,
                createdAt: now,
                updatedAt: now));
        }

        return result;
    }

    /// <summary>
    /// Strips <c>```json</c> / <c>```</c> code fences from around the JSON body.
    /// </summary>
    /// <param name="text">The raw text that may contain code fences.</param>
    /// <returns>The JSON body without fences.</returns>
    private static string StripCodeFences(string text)
    {
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            text = text["```json".Length..].TrimStart();
        }
        else if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text["```".Length..].TrimStart();
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3].TrimEnd();
        }

        return text;
    }

    /// <summary>
    /// Extracts the first balanced <c>{...}</c> object from <paramref name="text"/>, ignoring any
    /// prose before or after it.
    /// </summary>
    /// <remarks>
    /// Brace counting is string-aware: a <c>{</c> or <c>}</c> inside a JSON string literal does not
    /// change the depth, so a record whose <c>Value</c> contains braces (Markdown, code snippets)
    /// is not truncated mid-object. Escapes are honoured so <c>\"</c> does not end a string.
    /// </remarks>
    /// <param name="text">Text that may contain a JSON object surrounded by prose.</param>
    /// <param name="json">The extracted object, or <see cref="string.Empty"/> when none was found.</param>
    /// <returns><see langword="true"/> when a balanced object was found.</returns>
    private static bool TryExtractJsonObject(string text, out string json)
    {
        json = string.Empty;

        int start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        bool inString = false;
        bool escaped = false;
        int depth = 0;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(i + 1)];
                    return true;
                }
            }
        }

        // Unbalanced (e.g. a truncated response) — leave the text alone so the JSON error surfaces.
        return false;
    }

    private static void ValidateRequiredFields(ExtractionRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.ContentType))
        {
            throw new ExtractionParseException("Record missing required field 'ContentType'.");
        }

        if (string.IsNullOrWhiteSpace(r.Title))
        {
            throw new ExtractionParseException("Record missing required field 'Title'.");
        }

        if (string.IsNullOrWhiteSpace(r.Domain))
        {
            throw new ExtractionParseException("Record missing required field 'Domain'.");
        }

        if (string.IsNullOrWhiteSpace(r.Key))
        {
            throw new ExtractionParseException("Record missing required field 'Key'.");
        }

        if (string.IsNullOrWhiteSpace(r.Value))
        {
            throw new ExtractionParseException("Record missing required field 'Value'.");
        }
    }

    private static ContentType ParseContentType(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "FACT" => ContentType.Fact,
            "MEMORY" => ContentType.Memory,
            _ => throw new ExtractionParseException($"Unknown ContentType value: '{value}'. Expected 'Fact' or 'Memory'."),
        };

    private sealed class ExtractionRoot
    {
        public List<ExtractionRecord>? Records { get; set; }
    }

    private sealed class ExtractionRecord
    {
        public string? ContentType { get; set; }
        public string? Title { get; set; }
        public string? Domain { get; set; }
        public string? Key { get; set; }
        public List<string>? Tags { get; set; }
        public string? Scope { get; set; }
        public double? Importance { get; set; }
        public string? Value { get; set; }
    }
}
