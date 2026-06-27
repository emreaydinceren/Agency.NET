using System.Diagnostics;
using System.Text;

namespace Agency.Harness.Console.Test;

/// <summary>
/// End-to-end TDD tests for the Loop Kit integration in <c>Agency.Harness.Console</c>.
/// These tests verify the three last-mile wiring gaps are closed:
/// <list type="number">
///   <item><c>EnableGoalkeeperTool</c> / <c>DisableGoalkeeperTool</c> registered in the <c>ToolRegistry</c>.</item>
///   <item><c>AddAgencyLoop</c> called so <c>LoopOptions</c> binds from configuration.</item>
///   <item><c>ConsoleChatSession</c> drives turns through <c>LoopRunner.RunAsync</c>.</item>
/// </list>
/// Tests T-CON-LOOP-1 through T-CON-LOOP-4 are <b>RED</b> before the fix and <b>GREEN</b> after.
/// T-CON-LOOP-5 is a regression guard that is GREEN both before and after (verifies no loop
/// noise leaks onto plain turns when the rendering guard is applied correctly).
/// <para>
/// Run with:  <c>dotnet test --filter "Category=Functional"</c><br/>
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>
/// </para>
/// </summary>
[Trait("Category", "Functional")]
[Collection("ConsoleProcessTests")]
public sealed class LoopConsoleIntegrationTests
{
    private static readonly string ConsoleDll = GetConsoleDll();

    private static string GetConsoleDll()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tfm = parts[^1];
        string cfg = parts[^2];
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Agency.Harness.Console",
            "bin", cfg, tfm,
            "Agency.Harness.Console.dll"));
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ── T-CON-LOOP-1 ─────────────────────────────────────────────────────────

    /// <summary>
    /// T-CON-LOOP-1 — Verifies that <c>enable_goalkeeper</c> is registered in the runtime
    /// <c>ToolRegistry</c> and that calling it arms a goal, causing a <c>GoalSetEvent</c>
    /// to render the goal box in stdout.
    /// <para>
    /// <b>RED before fix:</b> The tool is not registered → the agent receives a "tool not found"
    /// error from the registry, <c>GoalState</c> is never armed, and no goal box appears.
    /// </para>
    /// <para>
    /// <b>GREEN after fix:</b> Tool registered + <c>LoopRunner</c> driven → <c>GoalSetEvent</c>
    /// emits → "Goal" box renders in stdout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task T_CON_LOOP_1_GoalkeeperTool_IsRegistered_GoalBoxRenders()
    {
        var (output, _) = await RunAsync(
            [
                "Call the enable_goalkeeper tool with condition='integration test condition' and maxTurns=1. Do not do any other work after calling the tool.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        // Tool not found → never executes → no goal box. After fix: tool executes, GoalSetEvent fires.
        Assert.True(output.Contains("Goal", StringComparison.Ordinal),
            $"Expected the goal box ('Goal') to appear in output, indicating enable_goalkeeper ran successfully.\nOutput:\n{output}");

        // If the tool was unknown, the model would report an error referencing the tool name.
        Assert.False(
            output.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("enable_goalkeeper", StringComparison.Ordinal),
            $"Output contains 'not found' alongside 'enable_goalkeeper', indicating the tool is still unregistered.\nOutput:\n{output}");
    }

    // ── T-CON-LOOP-2 ─────────────────────────────────────────────────────────

    /// <summary>
    /// T-CON-LOOP-2 — Verifies that loading the <c>refactor-loop</c> skill causes the agent
    /// to call <c>enable_goalkeeper</c> (pre-approved via <c>AllowedTools</c>), which arms a
    /// goal and causes the goal box and first <c>TurnStartedEvent</c> to render.
    /// <para>
    /// <b>RED before fix:</b> <c>enable_goalkeeper</c> not registered → skill instructions
    /// result in a "tool not found" error → <c>GoalState</c> never armed → no goal box.
    /// </para>
    /// <para>
    /// <b>GREEN after fix:</b> Tool registered + <c>LoopRunner</c> driven → <c>GoalSetEvent</c>
    /// and subsequent <c>TurnStartedEvent</c> render in stdout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task T_CON_LOOP_2_RefactorLoopSkill_ArmsGoalkeeper_GoalBoxRenders()
    {
        const string marker = "SKILL_ARMED";
        var (output, _) = await RunAsync(
            [
                $"Use the refactor-loop skill. The task is: write the word {marker} in your response. " +
                $"The goal condition is: the word {marker} appears in the conversation.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        // GoalSetEvent must have rendered the goal box.
        Assert.True(output.Contains("Goal", StringComparison.Ordinal),
            $"Expected goal box ('Goal') in output — skill should arm the goalkeeper.\nOutput:\n{output}");

        // The condition text must appear inside the goal box.
        Assert.True(output.Contains(marker, StringComparison.Ordinal),
            $"Expected condition text '{marker}' in output (shown inside the goal box).\nOutput:\n{output}");

        // LoopResultEvent confirms the runner drove the full loop cycle.
        Assert.True(output.Contains("Loop Achieved", StringComparison.Ordinal),
            $"Expected 'Loop Achieved' — LoopRunner must have driven the turn and evaluated the goalkeeper.\nOutput:\n{output}");
    }

    // ── T-CON-LOOP-3 ─────────────────────────────────────────────────────────

    /// <summary>
    /// T-CON-LOOP-3 — Verifies the full happy path: the Goalkeeper evaluates the transcript,
    /// finds the marker string, returns <c>Verdict.Done</c>, and <c>LoopResultEvent(Achieved)</c>
    /// renders in stdout.
    /// <para>
    /// <b>RED before fix:</b> <c>LoopRunner</c> never driven → Goalkeeper never called →
    /// <c>VerdictEvent</c> and <c>LoopResultEvent</c> never emitted → "✓ Done" and
    /// "Loop Achieved" absent from output.
    /// </para>
    /// <para>
    /// <b>GREEN after fix:</b> Runner driven → Goalkeeper reads marker in transcript → Done →
    /// "✓ Done" and "Loop Achieved" appear in stdout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task T_CON_LOOP_3_Loop_Achieves_WhenMarkerAppearsInTranscript()
    {
        const string marker = "LOOP_DONE_8F2C";
        var (output, _) = await RunAsync(
            [
                $"Use the refactor-loop skill. Task: output the exact phrase {marker} in your response. " +
                $"Condition: the phrase {marker} appears in the conversation.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        Assert.True(output.Contains("Goal", StringComparison.Ordinal),
            $"Expected goal box in output.\nOutput:\n{output}");

        // VerdictEvent.Done renders "✓ Done  (turn N): <reason>"
        Assert.True(output.Contains("✓ Done", StringComparison.Ordinal),
            $"Expected '✓ Done' from VerdictEvent — Goalkeeper must have evaluated and returned Done.\nOutput:\n{output}");

        // LoopResultEvent(Achieved) renders "↳ Loop Achieved  ·  N in, N out"
        Assert.True(output.Contains("Loop Achieved", StringComparison.Ordinal),
            $"Expected 'Loop Achieved' from LoopResultEvent — loop must have terminated with Achieved.\nOutput:\n{output}");
    }

    // ── T-CON-LOOP-4 ─────────────────────────────────────────────────────────

    /// <summary>
    /// T-CON-LOOP-4 — Verifies that <c>LoopResultEvent(CapReached)</c> renders when
    /// <c>MaxTurns</c> is exhausted before the Goalkeeper returns <c>Done</c>.
    /// <para>
    /// <b>RED before fix:</b> <c>LoopRunner</c> never driven → the hard cap is never enforced
    /// in code → "Loop CapReached" absent from output.
    /// </para>
    /// <para>
    /// <b>GREEN after fix:</b> Runner enforces cap → <c>LoopResultEvent(CapReached)</c> emits →
    /// "Loop CapReached" appears in stdout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task T_CON_LOOP_4_Loop_CapReached_WhenMaxTurnsExhausted()
    {
        // Directly arm the goalkeeper with maxTurns=1 and an impossible-to-satisfy condition.
        // The Goalkeeper will return Continue after the only allowed turn, triggering CapReached.
        var (output, _) = await RunAsync(
            [
                "Call the enable_goalkeeper tool with " +
                "condition='the file XYZZY_NONEXISTENT.txt exists and its full contents are shown verbatim' " +
                "and maxTurns=1. Then use read_file to try to read XYZZY_NONEXISTENT.txt.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        Assert.True(output.Contains("Goal", StringComparison.Ordinal),
            $"Expected goal box in output.\nOutput:\n{output}");

        // LoopResultEvent(CapReached) renders "↳ Loop CapReached  ·  N in, N out"
        Assert.True(output.Contains("Loop CapReached", StringComparison.Ordinal),
            $"Expected 'Loop CapReached' from LoopResultEvent — the turn cap must have been enforced.\nOutput:\n{output}");
    }

    // ── T-CON-LOOP-5 ─────────────────────────────────────────────────────────

    /// <summary>
    /// T-CON-LOOP-5 — Regression guard. Verifies that a plain turn with no goal armed
    /// produces <b>no</b> loop events in stdout after the rendering guard is applied.
    /// <para>
    /// When <c>LoopRunner</c> is used for all turns (fix 3), it emits <c>TurnStartedEvent</c>
    /// and <c>LoopResultEvent(Achieved)</c> even for no-goal turns. Without a rendering guard
    /// in <c>ConsoleChatSession</c> that suppresses these when no <c>GoalSetEvent</c> has been
    /// observed, loop noise would appear after every plain message. This test catches that regression.
    /// </para>
    /// <para>
    /// <b>GREEN before any fix</b> (bare <c>SendAsync</c> emits no loop events) <b>and</b> after
    /// full fix with the rendering guard applied.
    /// </para>
    /// </summary>
    [Fact]
    public async Task T_CON_LOOP_5_PlainTurn_WithNoGoal_EmitsNoLoopEvents()
    {
        var (output, _) = await RunAsync(
            [
                "What is 2 + 2? Reply with just the number.",
                "/exit",
            ]);

        Assert.DoesNotContain("Goal", output, StringComparison.Ordinal);
        Assert.DoesNotContain("↺ Turn", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Loop Achieved", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Loop CapReached", output, StringComparison.Ordinal);
    }

    // ── Helper: process runner ────────────────────────────────────────────────

    private static async Task<(string Output, int ExitCode)> RunAsync(
        IEnumerable<string> inputs,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{ConsoleDll}\"",
            WorkingDirectory = Path.GetDirectoryName(ConsoleDll),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["DOTNET_ENVIRONMENT"] = "Test";
        psi.Environment["Skills__Directories__0"] = Path.Combine(GetRepoRoot(), ".agency", "skills");

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask  = process.StandardError.ReadToEndAsync();

        foreach (var line in inputs)
        {
            await process.StandardInput.WriteLineAsync(line);
        }

        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Console process did not exit within {effectiveTimeout}. " +
                $"Partial stdout:\n{await outputTask}\n" +
                $"Partial stderr:\n{await errorTask}");
        }

        string output = await outputTask;
        string error  = await errorTask;
        if (!string.IsNullOrWhiteSpace(error))
        {
            output = $"{output}\n[stderr]\n{error}";
        }

        return (output, process.ExitCode);
    }
}
