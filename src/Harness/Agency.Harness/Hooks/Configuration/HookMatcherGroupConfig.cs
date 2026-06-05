namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookMatcherGroupConfig
{
    public string? Matcher { get; set; }
    public HookHandlerConfig[] Hooks { get; set; } = [];
}
