using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Test.Hooks.Configuration.Handlers;

[Trait("Category", "Process")]
public sealed class CommandHookHandlerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static HookPayload MakePayload() => new HookPayload { HookEventName = "PreToolUse" };

    private static string Shell => OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh";

    private static string[] ShellArgs(string cmd) =>
        OperatingSystem.IsWindows() ? ["-NoProfile", "-c", cmd] : ["-c", cmd];

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Command_Exit0_DenyJson_ProducesDenyOutput()
    {
        const string json = """{"hookSpecificOutput":{"permissionDecision":"deny","permissionDecisionReason":"blocked"}}""";
        string script = OperatingSystem.IsWindows()
            ? $"Write-Output '{json}'"
            : $"printf '%s' '{json}'";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script)
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);
        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.Json);
        Assert.True(output.Json.HasValue);
    }

    [Fact]
    public async Task Command_Exit2_ProducesDenyOutput()
    {
        string script = "exit 2";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script)
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);
        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(2, output.ExitCode);
    }

    [Fact]
    public async Task Command_Exit0_RewriteJson_ProducesRewriteOutput()
    {
        const string json = """{"tool_input":{"key":"rewritten"}}""";
        string script = OperatingSystem.IsWindows()
            ? $"Write-Output '{json}'"
            : $"printf '%s' '{json}'";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script)
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);
        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(0, output.ExitCode);
        Assert.NotNull(output.Json);
        Assert.True(output.Json.HasValue);
    }

    [Fact]
    public async Task Command_Exit0_NoJson_ProducesAllowOutput()
    {
        string script = OperatingSystem.IsWindows()
            ? "Write-Output 'not json at all'"
            : "echo 'not json at all'";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script)
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);
        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(0, output.ExitCode);
        Assert.Null(output.Json);
    }

    [Fact]
    public async Task Command_NonZeroNonTwo_ProducesNonBlockingError()
    {
        string script = "exit 1";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script)
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);
        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(1, output.ExitCode);
    }

    [Fact]
    public async Task Command_Timeout_KillsProcessTree_NonBlocking()
    {
        string script = OperatingSystem.IsWindows()
            ? "Start-Sleep 30"
            : "sleep 30";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script),
            Timeout = 1
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);
        HookHandlerOutput output = await handler.InvokeAsync(MakePayload(), CancellationToken.None);

        Assert.Equal(HookExitCodes.Timeout, output.ExitCode);
    }

    [Fact]
    public async Task Command_LargeStderr_NoDeadlock()
    {
        // Write 100 KB to stderr and 1 KB to stdout, then exit 0.
        string script = OperatingSystem.IsWindows()
            ? "Write-Error (\"x\" * 102400); Write-Output (\"y\" * 1024)"
            : "dd if=/dev/urandom bs=102400 count=1 2>&1 | cat >&2; echo ok";

        HookHandlerConfig cfg = new HookHandlerConfig
        {
            Command = Shell,
            Args = ShellArgs(script)
        };

        CommandHookHandler handler = new CommandHookHandler(cfg);

        Task<HookHandlerOutput> invokeTask = handler.InvokeAsync(MakePayload(), CancellationToken.None);
#pragma warning disable xUnit1051 // Task.Delay overload; no test CT needed here
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
#pragma warning restore xUnit1051

        Task completed = await Task.WhenAny(invokeTask, timeoutTask);
        Assert.Same(invokeTask, completed);

        HookHandlerOutput output = await invokeTask;
        Assert.Equal(0, output.ExitCode);
    }
}
