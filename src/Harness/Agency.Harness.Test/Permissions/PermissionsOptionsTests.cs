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

    [Fact]
    public void Bind_AbsentSection_EnabledDefaultsToTrue()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.True(options.Enabled);
    }

    [Fact]
    public void Bind_AbsentSection_OnUnresolvedDefaultsToAsk()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.Equal(UnresolvedBehavior.Ask, options.OnUnresolved);
    }

    [Fact]
    public void Bind_AbsentSection_AllowIsEmptyNonNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.NotNull(options.Allow);
        Assert.Empty(options.Allow);
    }

    [Fact]
    public void Bind_AbsentSection_DenyIsEmptyNonNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.NotNull(options.Deny);
        Assert.Empty(options.Deny);
    }

    [Fact]
    public void Bind_AbsentSection_LocalRulesPathIsNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.Null(options.LocalRulesPath);
    }

    [Fact]
    public void Bind_AbsentSection_ToolInputKeysIsEmptyNonNull()
    {
        PermissionsOptions options = new PermissionsOptions();

        Assert.NotNull(options.ToolInputKeys);
        Assert.Empty(options.ToolInputKeys);
    }

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

    [Fact]
    public void Validate_MalformedAllowEntry_ThrowsInvalidOperationException()
    {
        PermissionsOptions options = new PermissionsOptions
        {
            Allow = ["Tool("]
        };

        Assert.Throws<InvalidOperationException>(() => PermissionsOptionsValidator.Validate(options));
    }

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

    [Fact]
    public void Validate_MalformedDenyEntry_ThrowsInvalidOperationException()
    {
        PermissionsOptions options = new PermissionsOptions
        {
            Deny = ["BadRule("]
        };

        Assert.Throws<InvalidOperationException>(() => PermissionsOptionsValidator.Validate(options));
    }

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

    [Fact]
    public void Validate_EmptyAllowAndDeny_DoesNotThrow()
    {
        PermissionsOptions options = new PermissionsOptions();

        // Must not throw — empty arrays are always valid.
        PermissionsOptionsValidator.Validate(options);
    }
}
