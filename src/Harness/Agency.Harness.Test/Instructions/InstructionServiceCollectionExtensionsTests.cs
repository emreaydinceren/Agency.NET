using Agency.Harness.Instructions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Test.Instructions;

/// <summary>Tests for <see cref="InstructionServiceCollectionExtensions"/>.</summary>
public sealed class InstructionServiceCollectionExtensionsTests
{
    /// <summary>Verifies that AddAgencyInstructions registers IInstructionResolver as a singleton.</summary>
    [Fact]
    public void AddAgencyInstructions_RegistersResolverAsSingleton()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        // Act
        services.AddAgencyInstructions(config);
        var provider = services.BuildServiceProvider();

        // Assert
        var resolver = provider.GetService<IInstructionResolver>();
        Assert.NotNull(resolver);
        Assert.IsType<ProjectInstructionResolver>(resolver);

        // Verify singleton: same instance on multiple resolutions
        var resolver2 = provider.GetService<IInstructionResolver>();
        Assert.Same(resolver, resolver2);
    }

    /// <summary>Verifies that InstructionsOptions are registered and available after AddAgencyInstructions.</summary>
    [Fact]
    public void AddAgencyInstructions_RegistersInstructionsOptions()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        // Act
        services.AddAgencyInstructions(config);
        var provider = services.BuildServiceProvider();

        // Assert: options should be accessible with defaults
        var options = provider.GetRequiredService<IOptions<InstructionsOptions>>().Value;
        Assert.NotNull(options);
        Assert.True(options.Enabled);
        Assert.NotEmpty(options.FallbackFilenames);
    }

    /// <summary>Verifies that the resolver uses default filenames when none are configured.</summary>
    [Fact]
    public async Task AddAgencyInstructions_ResolverUsesDefaultFilenames()
    {
        // Arrange: create a temp directory with a default AGENTS.md file
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

            // Write only AGENTS.md (the default)
            var agentsPath = Path.Combine(tempDir, "AGENTS.md");
            await File.WriteAllTextAsync(agentsPath, "# Test Agents");

            var config = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // Act
            services.AddAgencyInstructions(config);
            var provider = services.BuildServiceProvider();
            var resolver = provider.GetRequiredService<IInstructionResolver>();

            var result = await resolver.ResolveAsync(tempDir);

            // Assert: AGENTS.md should be found with default configuration
            Assert.Single(result.Sources);
            Assert.Equal("# Test Agents", result.Sources[0].Content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>Verifies that an empty FallbackFilenames list throws ArgumentException when the resolver is constructed.</summary>
    [Fact]
    public void AddAgencyInstructions_WithNoFilenames_ThrowsOnConstruction()
    {
        // Arrange: programmatically create empty options to force validation
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        // Configure with empty filenames by using the post-configure hook to override defaults
        services.AddAgencyInstructions(config);
        services.Configure<InstructionsOptions>(opts => opts.FallbackFilenames = []);
        var provider = services.BuildServiceProvider();

        // Act & Assert: the exception is thrown when we try to resolve the IInstructionResolver
        var ex = Assert.Throws<ArgumentException>(() =>
            provider.GetRequiredService<IInstructionResolver>());

        Assert.Contains("FallbackFilenames", ex.Message);
    }
}
