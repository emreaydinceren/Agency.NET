using System.Text.Json;
using System.Threading.Channels;
using Agency.Harness.Contexts;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Distiller.Test.Stubs;
using Agency.Memory.Distiller.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test.Tools;

/// <summary>
/// Tests for <see cref="MarkGoalCompleteTool"/> (Spec §6.7.2, C.6).
/// </summary>
public sealed class MarkGoalCompleteToolTests
{
    private static (MarkGoalCompleteTool Tool, ChannelSessionRegistry Registry)
        CreateTool(string userId = "u1", string sessionId = "s1", int turnCount = 3)
    {
        var options = Options.Create(new DistillerOptions());
        var registry = new ChannelSessionRegistry(options, NullLogger<ChannelSessionRegistry>.Instance);

        var tool = new MarkGoalCompleteTool(
            registry,
            userId,
            sessionId,
            () => turnCount);

        return (tool, registry);
    }

    /// <summary>Invoking the tool enqueues a DistillationJob with GoalCompletion trigger.</summary>
    [Fact]
    public async Task Invoke_EnqueuesJobWithGoalCompletionTrigger_AndOptionalSummary()
    {
        var (tool, registry) = CreateTool(turnCount: 5);

        JsonElement input = JsonDocument.Parse("""{"summary":"SSL issue fixed"}""").RootElement;
        await tool.InvokeAsync(input, CancellationToken.None);

        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "s1");
        Assert.True(ch.Reader.TryRead(out DistillationJob? job));
        Assert.Equal(DistillationTrigger.GoalCompletion, job!.Trigger);
        Assert.Equal("SSL issue fixed", job.TriggerSummary);
        Assert.Equal(5, job.UpToTurnIndex);
        Assert.Equal("u1", job.UserId);
        Assert.Equal("s1", job.SessionId);
    }

    /// <summary>Invoking the tool without a summary enqueues a job without TriggerSummary.</summary>
    [Fact]
    public async Task Invoke_WithNoSummary_EnqueuesJobWithNullSummary()
    {
        var (tool, registry) = CreateTool();

        JsonElement input = JsonDocument.Parse("{}").RootElement;
        await tool.InvokeAsync(input, CancellationToken.None);

        Channel<DistillationJob> ch = registry.GetOrCreate("u1", "s1");
        Assert.True(ch.Reader.TryRead(out DistillationJob? job));
        Assert.Null(job!.TriggerSummary);
    }

    /// <summary>The tool returns a success ToolResult (not an error), so it does not stop the loop.</summary>
    [Fact]
    public async Task Invoke_DoesNotStopLoop_ReturnsSuccessToolResult()
    {
        var (tool, _) = CreateTool();

        JsonElement input = JsonDocument.Parse("{}").RootElement;
        var result = await tool.InvokeAsync(input, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("complete", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
