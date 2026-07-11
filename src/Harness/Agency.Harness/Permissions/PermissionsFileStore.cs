using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.Harness.Permissions;

/// <summary>
/// Loads and appends permission rules to a local JSON file (<c>permissions.local.json</c>).
/// </summary>
/// <remarks>
/// File format: <c>{ "Allow": [ "Rule1", ... ], "Deny": [ "Rule2", ... ] }</c>.
/// <para>
/// <see cref="Load"/> is tolerant: a missing file, an empty file, corrupt JSON, and malformed
/// rule entries are all handled gracefully — each yields empty lists or is silently skipped.
/// </para>
/// <para>
/// <see cref="Append"/> opens the file exclusively (<see cref="FileShare.None"/>) and retries
/// up to ~10 times with ~50 ms backoff on <see cref="IOException"/> (sharing violation).
/// Give-up is logged as a warning (if a logger is present) and swallowed — the in-process
/// session grant still applies; persistence is best-effort.
/// </para>
/// </remarks>
internal sealed partial class PermissionsFileStore
{
    private const int MaxRetryAttempts = 10;
    private const int RetryDelayMs = 50;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger _logger;

    internal PermissionsFileStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Reads the local rules file and returns parsed allow and deny lists.
    /// Returns empty lists for any of: missing file, empty file, malformed JSON,
    /// or entries that fail <see cref="PermissionRule.TryParse"/>. Never throws.
    /// Does NOT create the file when it is missing.
    /// </summary>
    internal (List<PermissionRule> Allow, List<PermissionRule> Deny) Load()
    {
        if (!File.Exists(_path))
        {
            return ([], []);
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch
        {
            return ([], []);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return ([], []);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ([], []);
        }

        using (doc)
        {
            List<PermissionRule> allow = ParseArray(doc.RootElement, "Allow");
            List<PermissionRule> deny = ParseArray(doc.RootElement, "Deny");
            return (allow, deny);
        }
    }

    /// <summary>
    /// Appends <paramref name="rule"/> to the Allow or Deny array of the local rules file.
    /// Creates the file when it does not exist. Skips append silently when the rule is already
    /// present. Uses an exclusive file handle with retry/backoff on contention (spec §8).
    /// Give-up after ~10 attempts is logged as a warning and swallowed — never throws.
    /// </summary>
    internal void Append(string rule, bool deny)
    {
        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(RetryDelayMs);
            }

            if (!TryOpenExclusive(out FileStream? fs))
            {
                if (attempt == MaxRetryAttempts - 1)
                {
                    LogGaveUpAppendingRule(rule, _path, MaxRetryAttempts);
                }
                continue;
            }

            using (fs)
            {
                AppendUnderHandle(fs!, rule, deny);
            }

            return;
        }
    }

    private bool TryOpenExclusive(out FileStream? fs)
    {
        try
        {
            fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            fs = null;
            return false;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void AppendUnderHandle(FileStream fs, string rule, bool deny)
    {
        // Read current content.
        string json = string.Empty;
        if (fs.Length > 0)
        {
            using StreamReader reader = new(fs, leaveOpen: true);
            json = reader.ReadToEnd();
        }

        // Parse or start fresh.
        List<string> allowRules;
        List<string> denyRules;

        if (string.IsNullOrWhiteSpace(json))
        {
            allowRules = [];
            denyRules = [];
        }
        else
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                allowRules = ReadStringArray(doc.RootElement, "Allow");
                denyRules = ReadStringArray(doc.RootElement, "Deny");
            }
            catch (JsonException)
            {
                allowRules = [];
                denyRules = [];
            }
        }

        // Append if not already present.
        List<string> targetList = deny ? denyRules : allowRules;
        if (targetList.Contains(rule, StringComparer.Ordinal))
        {
            return;
        }

        targetList.Add(rule);

        // Serialize.
        var document = new { Allow = allowRules, Deny = denyRules };
        string newJson = JsonSerializer.Serialize(document, WriteOptions);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(newJson);

        // Overwrite: seek to start, truncate, write.
        fs.Seek(0, SeekOrigin.Begin);
        fs.SetLength(0);
        fs.Write(bytes);
        fs.Flush();
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> result = [];
        foreach (JsonElement el in arr.EnumerateArray())
        {
            string? s = el.GetString();
            if (s is not null)
            {
                result.Add(s);
            }
        }

        return result;
    }

    private static List<PermissionRule> ParseArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PermissionRule> result = [];
        foreach (JsonElement el in arr.EnumerateArray())
        {
            string? text = el.GetString();
            if (text is not null && PermissionRule.TryParse(text, out PermissionRule? rule))
            {
                result.Add(rule!);
            }
        }

        return result;
    }

    /// <summary>Logs that appending a rule to the local permissions file was abandoned after exhausting retries.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "PermissionsFileStore: gave up appending rule '{Rule}' to '{Path}' after {Attempts} attempts.")]
    private partial void LogGaveUpAppendingRule(string rule, string path, int attempts);
}
