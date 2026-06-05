namespace Agency.Harness.Hooks.Configuration;

internal enum HookEventName
{
    SessionStart,
    UserPromptSubmit,
    PreIteration,
    PreToolUse,
    PostToolUse,
    PostToolBatch,
    AssistantTurn,
    Stop,
    SessionEnd
}
