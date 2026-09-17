using Agency.Memory.Common.Options;
using Agency.Memory.Distiller.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Verifies that <see cref="MemoryServiceCollectionExtensions.AddAgencyMemory"/> actually binds
/// <see cref="MemoryOptions"/> and <see cref="DistillerOptions"/> from configuration (spec §14):
/// before this fix, both <c>AddOptions&lt;T&gt;()</c> call sites had no <c>.Bind(...)</c>, so a
/// configured <c>Memory:RetrievalTopK</c> or <c>Distiller:InactivityTimeout</c> was silently
/// ignored and every value came back as the hardcoded default.
/// </summary>
public sealed class MemoryServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:RetrievalTopK"] = "42",
                ["Distiller:InactivityTimeout"] = "00:09:00",
            })
            .Build();

    /// <summary>
    /// <see cref="MemoryOptions.RetrievalTopK"/> reflects the configured value, not the default.
    /// </summary>
    [Fact]
    public void AddAgencyMemory_BindsMemoryOptionsFromConfiguration()
    {
        var services = new ServiceCollection();
        services.AddAgencyMemory(BuildConfiguration());
        ServiceProvider provider = services.BuildServiceProvider();

        MemoryOptions opts = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        Assert.Equal(42, opts.RetrievalTopK);
    }

    /// <summary>
    /// <see cref="DistillerOptions.InactivityTimeout"/> reflects the configured value, not the default.
    /// </summary>
    [Fact]
    public void AddAgencyMemory_BindsDistillerOptionsFromConfiguration()
    {
        var services = new ServiceCollection();
        services.AddAgencyMemory(BuildConfiguration());
        ServiceProvider provider = services.BuildServiceProvider();

        DistillerOptions opts = provider.GetRequiredService<IOptions<DistillerOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(9), opts.InactivityTimeout);
    }

    /// <summary>
    /// The existing <c>Action&lt;MemoryOptions&gt;</c> overload still applies, and applies AFTER
    /// configuration binding — a caller configuring in code can still override a bound value.
    /// </summary>
    [Fact]
    public void AddAgencyMemory_ConfigureActionAppliesAfterConfigurationBinding()
    {
        var services = new ServiceCollection();
        services.AddAgencyMemory(BuildConfiguration(), configureMemory: o => o.RetrievalTopK = 99);
        ServiceProvider provider = services.BuildServiceProvider();

        MemoryOptions opts = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        Assert.Equal(99, opts.RetrievalTopK);
    }
}
