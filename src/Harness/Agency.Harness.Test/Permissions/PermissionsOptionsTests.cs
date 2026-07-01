using System.Text;
using Agency.Harness.Permissions;
using Microsoft.Extensions.Configuration;

namespace Agency.Harness.Test.Permissions;

/// <summary>
/// Verifies binding and validation semantics of <see cref="PermissionsOptions"/>,
/// <see cref="UnresolvedBehavior"/>, and <see cref="PermissionsOptionsValidator"/>:
/// full-section binding, default values, enum-from-string, case-insensitive ToolInputKeys,
/// and fail-fast validation on malformed rule strings.
/// </summary>
public sealed class PermissionsOptionsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PermissionsOptions Bind(string json)
    {
        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection("Permissions")
            .Get<PermissionsOptions>() ?? new PermissionsOptions();
    }

    // ── Binding: full section ─────────────────────────────────────────────────

    /// <summary>
    /// Binding a full "Permissions" configuration section with <c>Enabled: false</c> must set
    /// <see cref="PermissionsOptions.Enabled"/> to <see langword="false"/>.
    /// </summary>
    [Fact]
    public void Bind_FullSection_EnabledFalse_BindsCorrectly()
    {
        const string json = """
            {
              "Permissions": {
                "Enabled": false,
                "Allow": [ "ReadFile", "ExecutePowershell(git status*)" ],
                "Deny":  [ "WriteFile(E:/secrets/**)" ],
                "OnUnresolved": "Deny",
                "ToolInputKeys": { "mcp__gitea__get_file_contents": "filepath" },
                "LocalRulesPath": "/custom/permissions.local.json"
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.False(options.Enabled);
    }

    /// <summary>
    /// Binding must populate <see cref="PermissionsOptions.Allow"/> with every entry from the
    /// configured "Allow" array, in order.
    /// </summary>
    [Fact]
    public void Bind_FullSection_AllowArray_BindsAllEntries()
    {
        const string json = """
            {
              "Permissions": {
                "Allow": [ "ReadFile", "ExecutePowershell(git status*)" ]
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.NotNull(options.Allow);
        Assert.Equal(2, options.Allow.Length);
        Assert.Contains("ReadFile", options.Allow);
        Assert.Contains("ExecutePowershell(git status*)", options.Allow);
    }

    /// <summary>
    /// Binding must populate <see cref="PermissionsOptions.Deny"/> with every entry from the
    /// configured "Deny" array.
    /// </summary>
    [Fact]
    public void Bind_FullSection_DenyArray_BindsAllEntries()
    {
        const string json = """
            {
              "Permissions": {
                "Deny": [ "WriteFile(E:/secrets/**)" ]
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.NotNull(options.Deny);
        Assert.Single(options.Deny);
        Assert.Equal("WriteFile(E:/secrets/**)", options.Deny[0]);
    }

    /// <summary>
    /// The string <c>"Deny"</c> in the "OnUnresolved" configuration key must bind to
    /// <see cref="UnresolvedBehavior.Deny"/>.
    /// </summary>
    [Fact]
    public void Bind_OnUnresolved_Deny_BindsToUnresolvedBehaviorDeny()
    {
        const string json = """
            {
              "Permissions": {
                "OnUnresolved": "Deny"
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.Equal(UnresolvedBehavior.Deny, options.OnUnresolved);
    }

    /// <summary>
    /// The string <c>"Ask"</c> in the "OnUnresolved" configuration key must bind to
    /// <see cref="UnresolvedBehavior.Ask"/>.
    /// </summary>
    [Fact]
    public void Bind_OnUnresolved_Ask_BindsToUnresolvedBehaviorAsk()
    {
        const string json = """
            {
              "Permissions": {
                "OnUnresolved": "Ask"
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.Equal(UnresolvedBehavior.Ask, options.OnUnresolved);
    }

    /// <summary>
    /// Binding must populate <see cref="PermissionsOptions.ToolInputKeys"/> with every
    /// tool-name/key-field pair from the configured "ToolInputKeys" object.
    /// </summary>
    [Fact]
    public void Bind_ToolInputKeys_BindsDictionaryEntries()
    {
        const string json = """
            {
              "Permissions": {
                "ToolInputKeys": {
                  "mcp__gitea__get_file_contents": "filepath",
                  "MyCustomTool": "command"
                }
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.NotNull(options.ToolInputKeys);
        Assert.Equal(2, options.ToolInputKeys.Count);
    }

    /// <summary>
    /// Because <see cref="IConfiguration"/> binding may replace the dictionary instance and
    /// lose its comparer, <see cref="PermissionsOptions.ToolInputKeys"/> must guarantee
    /// case-insensitive lookups after binding regardless of the casing used to configure or
    /// query a key.
    /// </summary>
    [Fact]
    public void Bind_ToolInputKeys_LookupIsCaseInsensitive()
    {
        // IConfiguration binding may replace the dictionary instance, losing OrdinalIgnoreCase.
        // The implementation (Task 04) must guarantee case-insensitive lookups after binding.
        // This test asserts the required behavior: a key bound with one casing is retrievable
        // using a different casing.
        const string json = """
            {
              "Permissions": {
                "ToolInputKeys": {
                  "mcp__gitea__get_file_contents": "filepath"
                }
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        // Look up with different casings — all must succeed.
        Assert.True(options.ToolInputKeys.TryGetValue("MCP__GITEA__GET_FILE_CONTENTS", out string? val1));
        Assert.Equal("filepath", val1);

        Assert.True(options.ToolInputKeys.TryGetValue("Mcp__Gitea__Get_File_Contents", out string? val2));
        Assert.Equal("filepath", val2);

        Assert.True(options.ToolInputKeys.ContainsKey("mcp__gitea__get_file_contents"));
    }

    /// <summary>
    /// Binding must populate <see cref="PermissionsOptions.LocalRulesPath"/> from the
    /// configured "LocalRulesPath" value.
    /// </summary>
    [Fact]
    public void Bind_FullSection_LocalRulesPath_BindsCorrectly()
    {
        const string json = """
            {
              "Permissions": {
                "LocalRulesPath": "/custom/permissions.local.json"
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.Equal("/custom/permissions.local.json", options.LocalRulesPath);
    }

    // ── Binding: defaults when section is absent or partial ───────────────────

    /// <summary>
    /// The default value of <see cref="PermissionsOptions.Enabled"/> must be
    /// <see langword="true"/> when no configuration is bound.
    /// </summary>
    [Fact]
    public void Bind_AbsentSection_EnabledDefaultsToTrue()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.True(options.Enabled);
    }

    /// <summary>
    /// The default value of <see cref="PermissionsOptions.OnUnresolved"/> must be
    /// <see cref="UnresolvedBehavior.Ask"/> when no configuration is bound.
    /// </summary>
    [Fact]
    public void Bind_AbsentSection_OnUnresolvedDefaultsToAsk()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.Equal(UnresolvedBehavior.Ask, options.OnUnresolved);
    }

    /// <summary>
    /// The default value of <see cref="PermissionsOptions.Allow"/> must be a non-null, empty
    /// array when no configuration is bound.
    /// </summary>
    [Fact]
    public void Bind_AbsentSection_AllowIsEmptyNonNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.NotNull(options.Allow);
        Assert.Empty(options.Allow);
    }

    /// <summary>
    /// The default value of <see cref="PermissionsOptions.Deny"/> must be a non-null, empty
    /// array when no configuration is bound.
    /// </summary>
    [Fact]
    public void Bind_AbsentSection_DenyIsEmptyNonNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.NotNull(options.Deny);
        Assert.Empty(options.Deny);
    }

    /// <summary>
    /// The default value of <see cref="PermissionsOptions.LocalRulesPath"/> must be
    /// <see langword="null"/> when no configuration is bound.
    /// </summary>
    [Fact]
    public void Bind_AbsentSection_LocalRulesPathIsNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.Null(options.LocalRulesPath);
    }

    /// <summary>
    /// The default value of <see cref="PermissionsOptions.ToolInputKeys"/> must be a non-null,
    /// empty dictionary when no configuration is bound.
    /// </summary>
    [Fact]
    public void Bind_AbsentSection_ToolInputKeysIsEmptyNonNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.NotNull(options.ToolInputKeys);
        Assert.Empty(options.ToolInputKeys);
    }

    /// <summary>
    /// When only one property is specified in configuration, every unspecified property of
    /// <see cref="PermissionsOptions"/> must retain its default value rather than being reset.
    /// </summary>
    [Fact]
    public void Bind_PartialSection_UnspecifiedPropertiesRetainDefaults()
    {
        // Only Enabled is set; everything else must retain its default value.
        const string json = """
            {
              "Permissions": {
                "Enabled": false
              }
            }
            """;

        PermissionsOptions options = Bind(json);

        Assert.Equal(UnresolvedBehavior.Ask, options.OnUnresolved);
        Assert.NotNull(options.Allow);
        Assert.Empty(options.Allow);
        Assert.NotNull(options.Deny);
        Assert.Empty(options.Deny);
        Assert.Null(options.LocalRulesPath);
    }

    // ── Validation: malformed Allow entries ───────────────────────────────────

    /// <summary>
    /// <see cref="PermissionsOptionsValidator.Validate"/> must throw
    /// <see cref="InvalidOperationException"/> when <see cref="PermissionsOptions.Allow"/>
    /// contains a malformed rule string.
    /// </summary>
    [Fact]
    public void Validate_MalformedAllowEntry_ThrowsInvalidOperationException()
    {
        PermissionsOptions options = new PermissionsOptions
        {
            Allow = ["Tool("]
        };

        Assert.Throws<InvalidOperationException>(() => PermissionsOptionsValidator.Validate(options));
    }

    /// <summary>
    /// The exception thrown by <see cref="PermissionsOptionsValidator.Validate"/> for a
    /// malformed allow rule must include the offending rule text in its message.
    /// </summary>
    [Fact]
    public void Validate_MalformedAllowEntry_ExceptionMessageContainsOffendingRule()
    {
        PermissionsOptions options = new PermissionsOptions
        {
            Allow = ["Tool("]
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PermissionsOptionsValidator.Validate(options));

        Assert.Contains("Tool(", ex.Message);
    }

    // ── Validation: malformed Deny entries ────────────────────────────────────

    /// <summary>
    /// <see cref="PermissionsOptionsValidator.Validate"/> must throw
    /// <see cref="InvalidOperationException"/> when <see cref="PermissionsOptions.Deny"/>
    /// contains a malformed rule string.
    /// </summary>
    [Fact]
    public void Validate_MalformedDenyEntry_ThrowsInvalidOperationException()
    {
        PermissionsOptions options = new PermissionsOptions
        {
            Deny = ["BadRule("]
        };

        Assert.Throws<InvalidOperationException>(() => PermissionsOptionsValidator.Validate(options));
    }

    /// <summary>
    /// The exception thrown by <see cref="PermissionsOptionsValidator.Validate"/> for a
    /// malformed deny rule must include the offending rule text in its message.
    /// </summary>
    [Fact]
    public void Validate_MalformedDenyEntry_ExceptionMessageContainsOffendingRule()
    {
        PermissionsOptions options = new PermissionsOptions
        {
            Deny = ["BadRule("]
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PermissionsOptionsValidator.Validate(options));

        Assert.Contains("BadRule(", ex.Message);
    }

    // ── Validation: valid rules pass without throwing ─────────────────────────

    /// <summary>
    /// <see cref="PermissionsOptionsValidator.Validate"/> must not throw for a set of
    /// spec-valid rules: bare, parameterized, and MCP-wildcard forms in both Allow and Deny.
    /// </summary>
    [Fact]
    public void Validate_ValidRules_DoesNotThrow()
    {
        // Bare rule, parameterized rule, and MCP wildcard — all spec-valid.
        PermissionsOptions options = new PermissionsOptions
        {
            Allow =
            [
                "ReadFile",
                "ExecutePowershell(git status*)",
                "mcp__gitea__list_*"
            ],
            Deny =
            [
                "WriteFile(E:/secrets/**)"
            ]
        };

        // Must not throw.
        PermissionsOptionsValidator.Validate(options);
    }

    /// <summary>
    /// <see cref="PermissionsOptionsValidator.Validate"/> must not throw when both
    /// <see cref="PermissionsOptions.Allow"/> and <see cref="PermissionsOptions.Deny"/> are
    /// empty — an empty rule set is always valid.
    /// </summary>
    [Fact]
    public void Validate_EmptyAllowAndDeny_DoesNotThrow()
    {
        PermissionsOptions options = new PermissionsOptions();

        // Must not throw — empty arrays are always valid.
        PermissionsOptionsValidator.Validate(options);
    }
}
