using Agency.Memory.Common.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for options records and their default values.</summary>
public sealed class OptionsTests
{
    /// <summary>Verifies that <see cref="MemoryOptions"/> defaults match the specification.</summary>
    [Fact]
    public void MemoryOptions_DefaultsMatchSpec()
    {
        var opts = new MemoryOptions();

        Assert.Equal("agency_memory", opts.CollectionName);
        Assert.Equal(10, opts.RetrievalTopK);
        Assert.Equal(3, opts.OverFetchFactor);
        Assert.Equal(0.2, opts.ImportancePruneThreshold);
        Assert.Equal(TimeSpan.FromDays(30), opts.StalePruneAge);
        Assert.Equal(TimeSpan.FromHours(24), opts.HygieneSchedule);
    }

    /// <summary>Verifies that <see cref="DistillerOptions"/> defaults match the specification.</summary>
    [Fact]
    public void DistillerOptions_DefaultsMatchSpec()
    {
        var opts = new DistillerOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), opts.InactivityTimeout);
        Assert.Equal(3, opts.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2), opts.RetryBaseDelay);
        Assert.Equal(32, opts.PerSessionQueueCapacity);
        Assert.Equal(BackpressurePolicy.DropOldest, opts.Backpressure);
    }

    /// <summary>Verifies that <see cref="ConsolidatorOptions"/> defaults match the specification.</summary>
    [Fact]
    public void ConsolidatorOptions_DefaultsMatchSpec()
    {
        var opts = new ConsolidatorOptions();

        Assert.Equal(ConsolidationTrigger.OnSessionEnd, opts.Trigger);
        Assert.Equal(20, opts.MaxIterations);
        Assert.Equal(0.50m, opts.MaxCostUsd);
    }

    /// <summary>Verifies that <see cref="MemoryOptions"/> can be bound from an <see cref="IConfiguration"/>.</summary>
    [Fact]
    public void MemoryOptions_BindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:CollectionName"] = "custom_memory",
                ["Memory:RetrievalTopK"] = "5",
                ["Memory:OverFetchFactor"] = "2",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<MemoryOptions>(configuration.GetSection("Memory"));
        var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        Assert.Equal("custom_memory", opts.CollectionName);
        Assert.Equal(5, opts.RetrievalTopK);
        Assert.Equal(2, opts.OverFetchFactor);
    }
}
