namespace Agency.Harness.Hooks.Configuration;

internal enum HookHandlerKind
{
    Command,
    Http,
    McpTool,   // reserved — V2
    Prompt,    // reserved — V2
    Agent      // reserved — V2
}
