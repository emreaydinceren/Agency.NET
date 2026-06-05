namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookHandlerConfig
{
    public HookHandlerKind Type { get; set; } = HookHandlerKind.Command;
    public string? Command { get; set; }
    public string[] Args { get; set; } = [];
    public int? Timeout { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}
