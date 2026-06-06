using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agency.Harness.Permissions;

/// <summary>
/// Evaluates tool calls against configuration allow/deny rules and runtime session grants.
/// Implements the algorithm specified in §5 of the Permission Model Design Document.
/// </summary>
/// <remarks>
/// Thread-safety: config lists are immutable after construction and read lock-free.
/// Session grant lists are guarded by <see cref="_grantedLock"/>.
/// <see cref="Evaluate"/> never blocks and never performs I/O.
/// </remarks>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    // Convention fallback order per spec §4.3 step 2.
    private static readonly string[] ConventionKeys = ["command", "path", "file_path", "url"];

    // Built-in default key-field mappings (spec §4.3 step 1 table).
    private static readonly Dictionary<string, string> BuiltinDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ExecutePowershell"] = "command",
            ["ReadFile"] = "path",
            ["WriteFile"] = "path",
        };

    private readonly PermissionsOptions _options;

    // Merged tool-input-key map: user config wins on conflict (spec §4.3 step 1).
    private readonly Dictionary<string, string> _toolInputKeys;

    // Immutable after ctor — read without locking.
    private readonly List<PermissionRule> _configAllow;
    private readonly List<PermissionRule> _configDeny;

    // Lock-guarded; seeded from PermissionsFileStore.Load() in the ctor.
    private readonly Lock _grantedLock = new();
    private readonly List<PermissionRule> _grantedAllow;
    private readonly List<PermissionRule> _grantedDeny;

    private readonly PermissionsFileStore _store;
    private readonly ILogger<PermissionEvaluator>? _logger;

    public PermissionEvaluator(PermissionsOptions options, ILogger<PermissionEvaluator>? logger = null)
    {
        _options = options;
        _logger = logger;

        // Parse config rules (validated at startup; Parse here mirrors the spec ctor description).
        _configAllow = ParseRules(options.Allow);
        _configDeny = ParseRules(options.Deny);

        // Merge built-in defaults UNDER user entries: start with defaults, then overlay user map.
        _toolInputKeys = new Dictionary<string, string>(BuiltinDefaults, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in options.ToolInputKeys)
        {
            _toolInputKeys[entry.Key] = entry.Value;
        }

        // Resolve local rules path per spec §7.1.
        string localPath = options.LocalRulesPath
            ?? Path.Combine(AppContext.BaseDirectory, "permissions.local.json");

        _store = new PermissionsFileStore(localPath, logger);

        // Seed granted lists from persisted file — malformed entries already skipped by the store.
        (List<PermissionRule> seedAllow, List<PermissionRule> seedDeny) = _store.Load();
        _grantedAllow = seedAllow;
        _grantedDeny = seedDeny;
    }

    /// <inheritdoc/>
    public PermissionDecision Evaluate(string toolName, JsonElement input)
    {
        // Step 1: kill switch.
        if (!_options.Enabled)
        {
            return PermissionDecision.Allowed;
        }

        // Step 2: extract key value.
        string? keyValue = ExtractKeyValue(toolName, input);

        // Step 3: check deny rules — config first, then granted (both searched together per spec).
        PermissionRule? denyMatch = FindMatch(toolName, keyValue, _configDeny, _grantedDeny);
        if (denyMatch is not null)
        {
            return new PermissionDecision.Deny($"Permission rule '{denyMatch.Raw}' denies this call.");
        }

        // Step 4: check allow rules — config first, then granted.
        PermissionRule? allowMatch = FindMatch(toolName, keyValue, _configAllow, _grantedAllow);
        if (allowMatch is not null)
        {
            return PermissionDecision.Allowed;
        }

        // Step 5: unresolved.
        return _options.OnUnresolved == UnresolvedBehavior.Deny
            ? new PermissionDecision.Deny("No permission rule allows this call.")
            : new PermissionDecision.Ask(keyValue, BuildProposedRule(toolName, keyValue));
    }

    /// <inheritdoc/>
    public Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct)
    {
        PermissionRule rule = PermissionRule.Parse(proposedRule);

        lock (_grantedLock)
        {
            if (deny)
            {
                _grantedDeny.Add(rule);
            }
            else
            {
                _grantedAllow.Add(rule);
            }
        }

        // Append failures are logged and swallowed inside the store.
        _store.Append(proposedRule, deny);

        return Task.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the key value from <paramref name="input"/> for <paramref name="toolName"/>.
    /// Resolution order (spec §4.3):
    /// 1. Config map (merged with built-in defaults) — if the mapped property is present and is a string.
    /// 2. Convention fallback: first present string property among command, path, file_path, url.
    /// 3. Null when neither applies.
    /// </summary>
    private string? ExtractKeyValue(string toolName, JsonElement input)
    {
        // Step 1: config map lookup.
        if (_toolInputKeys.TryGetValue(toolName, out string? mappedProperty))
        {
            if (input.TryGetProperty(mappedProperty, out JsonElement mappedEl) &&
                mappedEl.ValueKind == JsonValueKind.String)
            {
                return mappedEl.GetString();
            }
            // Mapped tool whose mapped property is absent or non-string — fall through to convention.
        }

        // Step 2: convention fallback.
        foreach (string key in ConventionKeys)
        {
            if (input.TryGetProperty(key, out JsonElement el) &&
                el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }

        return null;
    }

    /// <summary>Searches <paramref name="primary"/> then <paramref name="secondary"/> for the first matching rule.</summary>
    private PermissionRule? FindMatch(
        string toolName,
        string? keyValue,
        List<PermissionRule> primary,
        List<PermissionRule> secondary)
    {
        // Config lists are immutable; granted lists need a lock snapshot to avoid races.
        foreach (PermissionRule rule in primary)
        {
            if (rule.Matches(toolName, keyValue))
            {
                return rule;
            }
        }

        List<PermissionRule> grantedSnapshot;
        lock (_grantedLock)
        {
            grantedSnapshot = [.. secondary];
        }

        foreach (PermissionRule rule in grantedSnapshot)
        {
            if (rule.Matches(toolName, keyValue))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>Builds the proposed rule string per spec §4.4.</summary>
    private static string BuildProposedRule(string toolName, string? keyValue) =>
        keyValue is not null ? $"{toolName}({keyValue})" : toolName;

    /// <summary>Parses an array of rule strings, skipping malformed entries (best-effort).</summary>
    private static List<PermissionRule> ParseRules(string[] rules)
    {
        List<PermissionRule> result = new(rules.Length);
        foreach (string r in rules)
        {
            result.Add(PermissionRule.Parse(r));
        }

        return result;
    }
}
