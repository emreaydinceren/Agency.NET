using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using Microsoft.Extensions.AI;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.Prompts;
using Agency.Memory.Consolidator.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Consolidator.Services;

/// <summary>
/// Factory that builds the consolidator sub-agent and returns a delegate suitable
/// for injection into <see cref="ConsolidatorBackgroundService"/> (Spec §6.3 / §8.4).
/// </summary>
/// <remarks>
/// The sub-agent is an instance of the existing <see cref="Agent"/> harness with:
/// <list type="bullet">
///   <item>The four consolidation tools: Merge, Update, Delete, Done.</item>
///   <item>The reconciliation prompt from <see cref="ConsolidatorReconciliationPrompt"/>.</item>
///   <item>Two stop conditions: <c>MaxIterations</c> and <c>MaxCostUsd</c>.</item>
/// </list>
/// The "done" stop condition fires when <see cref="MemoryDoneTool"/> sets a flag.
/// Because <see cref="StopCondition"/> only inspects the <see cref="Context"/>, the flag
/// is stored on a wrapper that the <see cref="StopCondition"/> delegate captures.
/// </remarks>
internal static class ConsolidatorSubAgentFactory
{
    /// <summary>
    /// Creates a <see cref="Func{T1,T2,T3,Task}"/> that, when invoked, runs the consolidator
    /// sub-agent for the given user over the provided record set.
    /// </summary>
    /// <param name="llm">The LLM client used by the sub-agent.</param>
    /// <param name="model">The model identifier forwarded to the LLM.</param>
    /// <param name="store">The memory store the tools operate on.</param>
    /// <param name="options">Consolidator configuration (MaxIterations, MaxCostUsd).</param>
    /// <param name="eventBus">
    /// Bus used to publish a <see cref="MemoryMutatedEvent"/> for each successful Merge / Update /
    /// Delete the sub-agent performs, so hosts can surface autonomous memory changes to the user
    /// (TI-8.3).
    /// </param>
    /// <param name="logger">Logger forwarded to the agent and used for user-facing mutation lines.</param>
    /// <param name="timeProvider">
    /// Optional time source forwarded to the sub-agent. When <see langword="null"/> the agent
    /// defaults to <see cref="TimeProvider.System"/> (production behaviour). Tests inject a fixed
    /// provider so the agent's "Current date/time (UTC)" system-prompt line is byte-stable, which
    /// keeps the LLM request bodies replayable from the HTTP response cache.
    /// </param>
    /// <param name="mergeIdFactory">
    /// Optional generator for the id of the record produced by <c>Memory_Merge</c>. When
    /// <see langword="null"/> a random GUID is used (production behaviour). Tests inject a
    /// deterministic factory so the merged-record id echoed back into later turns is stable and
    /// the multi-turn request bodies stay replayable from the cache.
    /// </param>
    /// <returns>
    /// A delegate <c>(userId, records, ct) => Task&lt;(int Merges, int Updates, int Deletes)&gt;</c>
    /// that drives the sub-agent to completion and returns the mutation tallies.
    /// </returns>
    internal static Func<string, IReadOnlyList<Record>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>> CreateRunner(
        IChatClient llm,
        string model,
        IMemoryStore store,
        IOptions<ConsolidatorOptions> options,
        IAsyncEventBus eventBus,
        ILogger<Agent>? logger = null,
        TimeProvider? timeProvider = null,
        Func<string>? mergeIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventBus);

        return async (userId, records, ct) =>
        {
            var opts = options.Value;

            // Done flag: set by Memory_Done; read by the stop condition.
            bool done = false;

            // Running mutation tallies — incremented as each MemoryMutatedEvent is emitted.
            int merges = 0;
            int updates = 0;
            int deletes = 0;

            // Build tools for this run.
            var mergeToolInstance = new MemoryMergeTool(store, userId, mergeIdFactory);
            var updateToolInstance = new MemoryUpdateTool(store, userId);
            var deleteToolInstance = new MemoryDeleteTool(store, userId);
            var doneTool = new MemoryDoneTool(onDone: () => { done = true; });

            var registry = new ToolRegistry();
            registry.Register(mergeToolInstance);
            registry.Register(updateToolInstance);
            registry.Register(deleteToolInstance);
            registry.Register(doneTool);

            // Stop conditions: Memory_Done called OR max iterations OR budget exceeded.
            var stop = StopConditions.Any(
                StopConditions.StepCountIs(opts.MaxIterations),
                StopConditions.BudgetExceeded(opts.MaxCostUsd),
                (ctx, _) => done);

            var agent = new Agent(
                llm: llm,
                model: model,
                clientType: "Consolidator",
                stopWhen: stop,
                logger: logger,
                timeProvider: timeProvider);

            // Build the initial prompt from the records dump.
            double factThreshold = 0.85;
            double memoryThreshold = 0.75;
            string prompt = ConsolidatorReconciliationPrompt.Render(
                userId: userId,
                records: records,
                maxIterations: opts.MaxIterations,
                factThreshold: factThreshold,
                memoryThreshold: memoryThreshold);

            var toolCtx = new ToolContext { Registry = registry };
            var session = new ChatSession(agent, new AgentOptions(), toolCtx);

            // Drive the agent to completion. Forward each successful memory-mutating tool call
            // onto the bus as a MemoryMutatedEvent so hosts can show the user what the agent
            // changed in its own long-term memory (TI-8.3).
            await foreach (AgentEvent evt in session.SendAsync(prompt, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (evt is ToolInvokedEvent { Result.IsError: false } tool
                    && TryMapMutation(tool.ToolName, out string operation))
                {
                    logger?.LogInformation(
                        "🧠 Memory {Operation} for user {UserId}: {Detail}",
                        operation, userId, tool.Result.Content);

                    // Increment the running tally for this operation type.
                    switch (operation)
                    {
                        case "Merge": merges++; break;
                        case "Update": updates++; break;
                        case "Delete": deletes++; break;
                    }

                    await eventBus.PublishAsync(
                        new MemoryMutatedEvent(userId, operation, tool.Result.Content), ct)
                        .ConfigureAwait(false);
                }
            }

            return (merges, updates, deletes);
        };
    }

    /// <summary>
    /// Maps a consolidator tool name to a user-facing mutation verb, or returns
    /// <see langword="false"/> for non-mutating tools (e.g. <c>Memory_Done</c>).
    /// </summary>
    /// <param name="toolName">The invoked tool's name.</param>
    /// <param name="operation">The mapped operation verb (<c>Merge</c> / <c>Update</c> / <c>Delete</c>).</param>
    /// <returns><see langword="true"/> when the tool mutated the store.</returns>
    private static bool TryMapMutation(string toolName, out string operation)
    {
        operation = toolName switch
        {
            "Memory_Merge" => "Merge",
            "Memory_Update" => "Update",
            "Memory_Delete" => "Delete",
            _ => string.Empty,
        };

        return operation.Length > 0;
    }
}
