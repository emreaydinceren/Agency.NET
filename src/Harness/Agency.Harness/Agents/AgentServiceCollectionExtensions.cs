using Microsoft.Extensions.DependencyInjection;

namespace Agency.Harness;

/// <summary>
/// Extension methods for registering the Agency agent-construction services with
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="Models"/>, the <see cref="IAgentFactory"/>, and a scoped default
    /// <see cref="Agent"/> built from <see cref="AgentOptions.DefaultClientName"/> /
    /// <see cref="AgentOptions.DefaultModel"/>.
    /// </summary>
    /// <remarks>
    /// Callers are responsible for binding <see cref="AgentOptions"/> (e.g. via
    /// <c>AddOptions&lt;AgentOptions&gt;().BindConfiguration("Agent")</c>) and for registering any
    /// optional <see cref="Permissions.IPermissionEvaluator"/> or <see cref="TimeProvider"/> the
    /// factory should pick up.
    /// </remarks>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddAgencyAgent(this IServiceCollection services)
    {
        services.AddTransient<Models>();
        services.AddScoped<IAgentFactory, AgentFactory>();
        services.AddScoped(sp => sp.GetRequiredService<IAgentFactory>().CreateAgent(null, null));
        return services;
    }
}
