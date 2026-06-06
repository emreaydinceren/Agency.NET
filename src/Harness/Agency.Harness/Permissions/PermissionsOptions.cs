namespace Agency.Harness.Permissions;

/// <summary>
/// Bound from the <c>Permissions</c> configuration section (appsettings.json).
/// </summary>
internal sealed class PermissionsOptions
{
    /// <summary>
    /// When <see langword="false"/> the evaluator short-circuits to Allow for every call.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rule strings that allow matching tool calls without prompting.</summary>
    public string[] Allow { get; set; } = [];

    /// <summary>Rule strings that deny matching tool calls.</summary>
    public string[] Deny { get; set; } = [];

    /// <summary>
    /// What to do when no rule matches a tool call.
    /// Default: <see cref="UnresolvedBehavior.Ask"/> (surface to the user).
    /// Use <see cref="UnresolvedBehavior.Deny"/> for headless / CI runs.
    /// </summary>
    public UnresolvedBehavior OnUnresolved { get; set; } = UnresolvedBehavior.Ask;

    /// <summary>
    /// Maps tool name → JSON property name used as the rule-match key value.
    /// Lookup is case-insensitive; the setter guarantees <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// even when <see cref="Microsoft.Extensions.Configuration"/> replaces the instance during binding.
    /// </summary>
    public Dictionary<string, string> ToolInputKeys
    {
        get => _toolInputKeys;
        set => _toolInputKeys = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Path to the per-machine user-grant file.
    /// <see langword="null"/> → default <c>permissions.local.json</c> next to the application.
    /// </summary>
    public string? LocalRulesPath { get; set; }

    // Backing field pre-initialized with OrdinalIgnoreCase so the default instance is also correct.
    private Dictionary<string, string> _toolInputKeys = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Controls what the evaluator does when no allow/deny rule matches a tool call.</summary>
internal enum UnresolvedBehavior
{
    /// <summary>Surface the call to the user as a <c>PermissionRequestedEvent</c> (default).</summary>
    Ask,

    /// <summary>
    /// Fail closed — deny the call without prompting.
    /// Use in headless / CI environments where nobody can answer permission requests.
    /// </summary>
    Deny,
}
