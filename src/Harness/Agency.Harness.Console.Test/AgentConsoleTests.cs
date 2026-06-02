using System.Diagnostics;
using System.Text;

namespace Agency.Harness.Console.Test;

/// <summary>
/// End-to-end tests for <c>Agency.Harness.Console</c>.
/// These tests spawn the real console binary, pipe input via stdin, and assert
/// on stdout — no fake LLM. All tests require LM Studio running at the address
/// configured in the console project's <c>appsettings.json</c>.
/// <para>
/// Run with:  <c>dotnet test --filter "Category=Functional"</c><br/>
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>
/// </para>
/// </summary>
[Trait("Category", "Functional")]
public sealed class AgentConsoleTests
{
    // Resolved once at class load time. The test binary sits at:
    //   src/Harness/Agency.Harness.Console.Test/bin/<cfg>/<tfm>/
    // The console binary (built as a ProjectReference dependency) sits at:
    //   src/Harness/Agency.Harness.Console/bin/<cfg>/<tfm>/Agency.Harness.Console.dll
    // We derive <cfg> and <tfm> from the last two segments of AppContext.BaseDirectory
    // so the path stays correct across Debug/Release and TFM changes.
    private static readonly string ConsoleDll = GetConsoleDll();

    private static string GetConsoleDll()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tfm = parts[^1];
        string cfg = parts[^2];
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",       // up to src/Harness/
            "Agency.Harness.Console",
            "bin", cfg, tfm,
            "Agency.Harness.Console.dll"));
    }

    // ── Startup / shutdown ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the welcome banner is printed before any user input is processed.
    /// Does not call the LLM (exits immediately).
    /// </summary>
    [Fact]
    public async Task WelcomeBanner_IsShown_OnStartup()
    {
        var (output, _) = await RunAsync(["/exit"], timeout: TimeSpan.FromSeconds(60));

        Assert.Contains("Agency", output);
        Assert.Contains("Agent Chat Console", output);
    }

    /// <summary>
    /// Verifies that the configured provider name and model are printed in the banner.
    /// Does not call the LLM (exits immediately).
    /// </summary>
    [Fact]
    public async Task Banner_ShowsProviderAndModel()
    {
        var (output, _) = await RunAsync(["/exit"], timeout: TimeSpan.FromSeconds(60));

        Assert.Contains("Provider", output);
        Assert.Contains("Model", output);
    }

    /// <summary>
    /// Verifies that "/exit" terminates the app cleanly with a session summary and exit code 0.
    /// Does not call the LLM.
    /// </summary>
    [Fact]
    public async Task Exit_Command_PrintsSessionSummary_AndExitsCleanly()
    {
        var (output, exitCode) = await RunAsync(["/exit"], timeout: TimeSpan.FromSeconds(60));

        Assert.Contains("Session ended", output);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Verifies that "quit" behaves identically to "/exit".
    /// Does not call the LLM.
    /// </summary>
    [Fact]
    public async Task Quit_Command_TerminatesCleanly()
    {
        var (output, exitCode) = await RunAsync(["quit"], timeout: TimeSpan.FromSeconds(60));

        Assert.Contains("Session ended", output);
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Verifies that a blank line is ignored and the REPL continues without crashing.
    /// Does not call the LLM.
    /// </summary>
    [Fact]
    public async Task EmptyInput_IsIgnored_ReplContinues()
    {
        var (output, exitCode) = await RunAsync(["", "/exit"], timeout: TimeSpan.FromSeconds(60));

        Assert.Contains("Session ended", output);
        Assert.Equal(0, exitCode);
    }

    // ── Single-turn conversation ───────────────────────────────────────────────

    /// <summary>
    /// Verifies the agent produces a non-empty response for a simple prompt.
    /// Requires LM Studio.
    /// </summary>
    [Fact]
    public async Task SingleTurn_AgentResponds_WithNonEmptyText()
    {
        var (output, _) = await RunAsync([
            "Reply with exactly one word: hello",
            "/exit",
        ]);

        Assert.True(ContainsAgentResponse(output), $"Expected an agent response marker in output. Output:\n{output}");
    }

    /// <summary>
    /// Verifies that the token-usage delta line is shown after each turn.
    /// Requires LM Studio.
    /// </summary>
    [Fact]
    public async Task SingleTurn_TokenStats_AreDisplayedAfterResponse()
    {
        var (output, _) = await RunAsync([
            "Reply with exactly one word: hello",
            "/exit",
        ]);

        Assert.True(ContainsAgentResponse(output), $"Expected an agent response marker in output. Output:\n{output}");
        Assert.Contains("↳ +", output);       // per-turn token delta line
        Assert.Contains("in,", output);        // "↳ +N in, +N out [Success]"
    }

    /// <summary>
    /// Verifies the session summary accumulates token counts across all turns.
    /// Requires LM Studio.
    /// </summary>
    [Fact]
    public async Task SingleTurn_SessionSummary_ShowsNonZeroTokens()
    {
        var (output, _) = await RunAsync([
            "What is 2 + 2? Reply with just the number.",
            "/exit",
        ]);

        Assert.Contains("Session ended", output);
        // Session summary contains "N in, N out total" — just verify it is not "0 in, 0 out".
        Assert.DoesNotContain("0 in, 0 out total", output);
    }

    // ── Multi-turn conversation ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that each user turn produces an [Agent] response, confirming that
    /// the conversation continues across multiple REPL iterations.
    /// Requires LM Studio.
    /// </summary>
    [Fact]
    public async Task MultiTurn_EachTurnProducesAgentResponse()
    {
        var (output, _) = await RunAsync(
            [
                "What is 2 + 2? Reply with just the number.",
                "What is 3 + 3? Reply with just the number.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        int agentLines = CountAgentResponses(output);
        Assert.True(agentLines >= 2,
            $"Expected at least 2 agent responses but found {agentLines}. Output:\n{output}");
    }

    /// <summary>
    /// Verifies the session-ended summary reports the correct turn count.
    /// Requires LM Studio.
    /// </summary>
    [Fact]
    public async Task MultiTurn_SessionSummary_ReflectsCorrectTurnCount()
    {
        var (output, _) = await RunAsync(
            [
                "What is 2 + 2? Reply with just the number.",
                "What is 3 + 3? Reply with just the number.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        Assert.Contains("2 turns", output);
    }

    /// <summary>
    /// Verifies that conversation context persists across turns: the agent's second
    /// answer should reference information given in the first turn.
    /// Requires LM Studio.
    /// </summary>
    [Fact]
    public async Task MultiTurn_ConversationContext_IsMaintained()
    {
        var (output, _) = await RunAsync(
            [
                "My name is John Doe. Just say 'Got it'.",
                "What is my name? Reply with just my name.",
                "/exit",
            ],
            timeout: TimeSpan.FromMinutes(3));

        // The agent must have received both turns and produced two responses.
        int agentLines = CountAgentResponses(output);
        Assert.True(agentLines >= 2,
            $"Expected at least 2 agent responses but found {agentLines}. Output:\n{output}");
    }

    // ── Helper: process runner ────────────────────────────────────────────────

    /// <summary>
    /// Starts the console app as a child process, pipes <paramref name="inputs"/>
    /// to stdin (one line each), closes stdin, then waits for the process to exit.
    /// </summary>
    /// <param name="inputs">Lines to write to the app's stdin in order.</param>
    /// <param name="timeout">Maximum time to wait for the process to exit.</param>
    /// <returns>All captured stdout and the process exit code.</returns>
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

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stdout/stderr on background tasks immediately to prevent the
        // OS pipe buffer from filling and deadlocking the child process.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask  = process.StandardError.ReadToEndAsync();

        // Write all input lines, then close stdin so the app sees EOF after
        // the last line and the REPL loop can exit naturally.
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

        // Process has exited; both streams are now fully drained.
        string output = await outputTask;
        string error = await errorTask;
        if (!string.IsNullOrWhiteSpace(error))
        {
            output = $"{output}\n[stderr]\n{error}";
        }

        return (output, process.ExitCode);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static int CountAgentResponses(string output)
    {
        int legacyAgentLines = CountOccurrences(output, "[Agent]");
        int currentAgentLines = CountOccurrences(output, "● ");
        return Math.Max(legacyAgentLines, currentAgentLines);
    }

    private static bool ContainsAgentResponse(string output)
    {
        return output.Contains("[Agent]", StringComparison.Ordinal)
            || output.Contains("● ", StringComparison.Ordinal);
    }
}
