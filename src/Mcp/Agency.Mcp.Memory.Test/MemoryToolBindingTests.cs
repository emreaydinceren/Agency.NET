using System.Reflection;
using System.Text.Json;
using Agency.KeyValueStore.Common;
using Microsoft.Extensions.AI;
using Moq;

namespace Agency.Mcp.Memory.Test;

/// <summary>
/// Exercises the MCP argument-binding layer — Microsoft.Extensions.AI <see cref="AIFunctionFactory"/>, the
/// same path the MCP server uses to expose <c>[McpServerTool]</c> methods. The binder marks a parameter as
/// REQUIRED unless it has a default value; a nullable annotation alone is NOT enough. These tests guard
/// against a regression where a model's partial or empty argument payload (<c>{}</c> or scope-only) is
/// rejected by the binder with an opaque "An error occurred invoking '…'." before the method body runs,
/// instead of reaching the body and returning an instructive, model-actionable error.
///
/// The in-process <see cref="MemoryToolTests"/> cannot catch this: calling the method in C# supplies every
/// argument, so the missing-required-argument throw only manifests through the binder.
/// </summary>
public sealed class MemoryToolBindingTests
{
    private static AIFunction Bind(MemoryTool tool, string methodName)
    {
        MethodInfo method = typeof(MemoryTool).GetMethod(methodName)!;
        return AIFunctionFactory.Create(method, tool);
    }

    private static MemoryTool ToolWithEmptyStore()
    {
        var store = new Mock<IKVStore>();
        store.Setup(s => s.SearchAsync<string>(It.IsAny<Query>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit<string>>());
        store.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit>());
        return new MemoryTool(store.Object);
    }

    private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    /// <summary>
    /// Recall with only the scope object must bind — the optional filters (domain/key/tags) must be treated
    /// as optional by the binder. This is the exact call that previously threw
    /// "missing a value for the required parameter 'domain'".
    /// </summary>
    [Fact]
    public async Task Recall_ScopeOnly_BindsWithoutRequiringOptionalFilters()
    {
        AIFunction fn = Bind(ToolWithEmptyStore(), nameof(MemoryTool.Recall));

        object? result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["scope"] = Json("{\"userId\":\"u1\"}")
        }, TestContext.Current.CancellationToken);

        Assert.Contains("[", result?.ToString());
    }

    /// <summary>
    /// Recall with no arguments at all (<c>{}</c>) must reach the body and return the instructive error,
    /// not be rejected by the binder.
    /// </summary>
    [Fact]
    public async Task Recall_EmptyArguments_ReturnsGracefulErrorInsteadOfThrowing()
    {
        AIFunction fn = Bind(ToolWithEmptyStore(), nameof(MemoryTool.Recall));

        object? result = await fn.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Contains("scope is required", result?.ToString());
    }

    /// <summary>list_global_keys with <c>{}</c> must reach the body and return the instructive error.</summary>
    [Fact]
    public async Task ListGlobalKeys_EmptyArguments_ReturnsGracefulErrorInsteadOfThrowing()
    {
        AIFunction fn = Bind(ToolWithEmptyStore(), nameof(MemoryTool.ListGlobalKeys));

        object? result = await fn.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Contains("scope is required", result?.ToString());
    }

    /// <summary>Memorize with <c>{}</c> must reach the body and return the instructive error.</summary>
    [Fact]
    public async Task Memorize_EmptyArguments_ReturnsGracefulErrorInsteadOfThrowing()
    {
        AIFunction fn = Bind(ToolWithEmptyStore(), nameof(MemoryTool.Memorize));

        object? result = await fn.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Contains("record is required", result?.ToString());
    }

    /// <summary>Forget with only scope must bind and report the still-missing domain via the body, not the binder.</summary>
    [Fact]
    public async Task Forget_ScopeOnly_ReturnsGracefulErrorInsteadOfThrowing()
    {
        AIFunction fn = Bind(ToolWithEmptyStore(), nameof(MemoryTool.Forget));

        object? result = await fn.InvokeAsync(new AIFunctionArguments
        {
            ["scope"] = Json("{\"userId\":\"u1\"}")
        }, TestContext.Current.CancellationToken);

        Assert.Contains("domain is required", result?.ToString());
    }
}
