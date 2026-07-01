
using System.Text;
using Agency.Harness.Hooks.Configuration;
using Microsoft.Extensions.Configuration;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies that the "Hooks" section in appsettings.json binds correctly
/// to <see cref="HooksOptions"/> via the IConfiguration binder.
/// </summary>
public sealed class HookConfigBindingTests
{
    private static HooksOptions? Bind(string json)
    {
        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection("Hooks")
            .Get<HooksOptions>();
    }

    /// <summary>A <c>PreToolUse</c> group with a matcher and a <c>Command</c> handler binds its matcher, command, args, and timeout.</summary>
    [Fact]
    public void Bind_PreToolUseGroup_PopulatesMatcherAndHandlers()
    {
        const string json = """
            {
              "Hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash|ExecutePowershell",
                    "hooks": [
                      {
                        "type": "Command",
                        "command": "pwsh",
                        "args": ["-File", "./hooks/guard.ps1"],
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }
            """;

        HooksOptions? options = Bind(json);

        Assert.NotNull(options);
        HookMatcherGroupConfig[] groups = options.Hooks[HookEventName.PreToolUse];
        Assert.Single(groups);

        HookMatcherGroupConfig group = groups[0];
        Assert.Equal("Bash|ExecutePowershell", group.Matcher);
        Assert.Single(group.Hooks);

        HookHandlerConfig handler = group.Hooks[0];
        Assert.Equal("pwsh", handler.Command);
        Assert.NotNull(handler.Args);
        Assert.Contains("-File", handler.Args);
        Assert.Contains("./hooks/guard.ps1", handler.Args);
        Assert.Equal(30, handler.Timeout);
    }

    /// <summary>String event-name keys in the JSON (<c>PreToolUse</c>, <c>PostToolUse</c>) bind to the corresponding <see cref="HookEventName"/> dictionary keys.</summary>
    [Fact]
    public void Bind_EnumKeyedDictionary_ParsesEventNames()
    {
        const string json = """
            {
              "Hooks": {
                "PreToolUse": [
                  {
                    "matcher": "*",
                    "hooks": [{ "type": "Command", "command": "echo" }]
                  }
                ],
                "PostToolUse": [
                  {
                    "matcher": "*",
                    "hooks": [{ "type": "Command", "command": "echo" }]
                  }
                ]
              }
            }
            """;

        HooksOptions? options = Bind(json);

        Assert.NotNull(options);
        Assert.True(options.Hooks.ContainsKey(HookEventName.PreToolUse));
        Assert.True(options.Hooks.ContainsKey(HookEventName.PostToolUse));
    }

    /// <summary>An <c>Http</c> handler binds its <c>url</c>, <c>headers</c>, and <c>timeout</c> keys.</summary>
    [Fact]
    public void Bind_HttpHandler_PopulatesUrlAndHeaders()
    {
        const string json = """
            {
              "Hooks": {
                "PreToolUse": [
                  {
                    "matcher": "*",
                    "hooks": [
                      {
                        "type": "Http",
                        "url": "http://localhost/hook",
                        "headers": { "X-Source": "test" },
                        "timeout": 10
                      }
                    ]
                  }
                ]
              }
            }
            """;

        HooksOptions? options = Bind(json);

        Assert.NotNull(options);
        HookHandlerConfig handler = options.Hooks[HookEventName.PreToolUse][0].Hooks[0];
        Assert.Equal("http://localhost/hook", handler.Url);
        Assert.NotNull(handler.Headers);
        Assert.Equal("test", handler.Headers["X-Source"]);
    }

    /// <summary>An empty <c>Hooks</c> section binds to <see cref="HooksOptions"/> with no entries, rather than <see langword="null"/> or throwing.</summary>
    [Fact]
    public void Bind_EmptyHooksSection_YieldsEmptyOptions()
    {
        const string json = """
            {
              "Hooks": {}
            }
            """;

        HooksOptions? options = Bind(json);

        Assert.Equal(0, options?.Hooks?.Count ?? 0);
    }

    /// <summary>The JSON <c>timeout</c> key binds to the handler config's <c>Timeout</c> property, interpreted in seconds.</summary>
    [Fact]
    public void Bind_TimeoutKey_MapsToTimeoutSeconds()
    {
        const string json = """
            {
              "Hooks": {
                "PreToolUse": [
                  {
                    "matcher": "*",
                    "hooks": [
                      {
                        "type": "Command",
                        "command": "echo",
                        "timeout": 10
                      }
                    ]
                  }
                ]
              }
            }
            """;

        HooksOptions? options = Bind(json);

        Assert.NotNull(options);
        HookHandlerConfig handler = options.Hooks[HookEventName.PreToolUse][0].Hooks[0];
        Assert.Equal(10, handler.Timeout);
    }

    /// <summary>An unrecognized event-name key under <c>Hooks</c> causes <c>HooksOptionsValidator.Validate</c> to throw with that key name in the message.</summary>
    [Fact]
    public void Bind_UnknownEventName_ThrowsWithKeyName()
    {
        const string json = """
            {
              "Hooks": {
                "NotAnEvent": []
              }
            }
            """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        var section = config.GetSection("Hooks");
        var options = section.Get<HooksOptions>() ?? new HooksOptions();
        var ex = Assert.Throws<InvalidOperationException>(
            () => HooksOptionsValidator.Validate(section, options));
        Assert.Contains("NotAnEvent", ex.Message);
    }
}
