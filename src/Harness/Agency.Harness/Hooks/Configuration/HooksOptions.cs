namespace Agency.Harness.Hooks.Configuration;

internal sealed class HooksOptions : Dictionary<HookEventName, HookMatcherGroupConfig[]>
{
    public Dictionary<HookEventName, HookMatcherGroupConfig[]> Hooks => this;
}
