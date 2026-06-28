using Agency.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Microsoft.Extensions.Configuration;

/// <summary>
/// Extension methods for <see cref="IConfigurationBuilder"/> that add Agency-specific
/// configuration sources and post-processing steps.
/// </summary>
/// <remarks>
/// Intended usage in <c>Program.cs</c>:
/// <code>
/// var builder = Host.CreateApplicationBuilder(args);   // registers appsettings, env vars, secrets, CLI
/// builder.Configuration.AddSharedConfiguration();      // inserts shared-appsettings.json at the FRONT (lowest precedence)
/// builder.Configuration.AddPlaceholderResolver();      // LAST — expands ${Section:Key} tokens
/// </code>
/// </remarks>
public static class AgencyConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a shared JSON configuration file as a configuration source so values common to
    /// multiple projects (host URLs, feature flags, etc.) can be defined once and merged into
    /// the normal configuration tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers a <see cref="JsonConfigurationSource"/> with <c>reloadOnChange: false</c>
    /// (reload-on-change is omitted intentionally — Agency settings are read once at startup;
    /// runtime reloads are handled by bespoke watchers).
    /// </para>
    /// <para>
    /// The source is <b>inserted at the front</b> of <see cref="IConfigurationBuilder.Sources"/>,
    /// giving it the <em>lowest</em> precedence. Because <see cref="Host.CreateApplicationBuilder(string[])"/>
    /// has already registered the standard host sources (appsettings, user-secrets, environment
    /// variables, command line) by the time this method runs, appending would make the shared file
    /// win over those. Inserting first instead means any of those higher-precedence sources — for
    /// example an <c>Agent__BaseUrl</c> environment variable — shadows the shared default, which is
    /// the intended override model. The call ordering relative to the resolver still matters
    /// (<see cref="AddPlaceholderResolver"/> must be last); ordering relative to other sources does not.
    /// </para>
    /// </remarks>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to. Must not be <see langword="null"/>.</param>
    /// <param name="path">
    /// Path to the shared JSON file. A relative path is resolved against the builder's configured
    /// <c>FileProvider</c> root (typically the application content root). Defaults to
    /// <c>shared-appsettings.json</c>.
    /// </param>
    /// <param name="optional">
    /// When <see langword="true"/> (the default), a missing or inaccessible file is silently
    /// ignored and the builder proceeds. When <see langword="false"/>, a missing file causes
    /// <see cref="IConfigurationBuilder.Build"/> to throw.
    /// </param>
    /// <returns>The same <see cref="IConfigurationBuilder"/> instance, for call chaining.</returns>
    public static IConfigurationBuilder AddSharedConfiguration(
        this IConfigurationBuilder builder,
        string path = "shared-appsettings.json",
        bool optional = true)
    {
        var source = new JsonConfigurationSource
        {
            FileProvider = null,
            Path = path,
            Optional = optional,
            ReloadOnChange = false,
        };

        // Resolve the file provider the same way AddJsonFile does (no-op for a relative path,
        // which is left to resolve against the builder's base path / content root at Build time).
        source.ResolveFileProvider();

        // Insert first, NOT append: the shared file must sit at the lowest precedence so the
        // standard host sources already registered by CreateApplicationBuilder can override it.
        builder.Sources.Insert(0, source);
        return builder;
    }

    /// <summary>
    /// Captures a snapshot of all merged configuration key/value pairs,
    /// expands <c>${Section:Key}</c> placeholder tokens in every value,
    /// and adds the expanded set as the last configuration source so that
    /// resolved values override their raw counterparts for every subsequent read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Snapshot strategy.</b> The current merged configuration is read once and
    /// materialised into an immutable <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// before any expansion occurs. The expanded values are then published through a
    /// dedicated <see cref="IConfigurationSource"/> appended last, giving them the
    /// highest precedence among all sources.
    /// </para>
    /// <para>
    /// <b>Safe against <c>ConfigurationManager</c>.</b>
    /// When <paramref name="builder"/> already implements
    /// <see cref="IConfigurationRoot"/> (for example the host's
    /// <c>builder.Configuration</c> property, which is a <c>ConfigurationManager</c>),
    /// the cast is performed directly rather than calling
    /// <see cref="IConfigurationBuilder.Build()"/>. This avoids the
    /// "wrap-and-clear" anti-pattern that self-destructs against
    /// <c>ConfigurationManager</c>, whose <c>Build()</c> returns <c>this</c>.
    /// </para>
    /// <para>
    /// Call this method <b>once, as the last configuration step</b>, after all other
    /// sources have been registered and before any configuration values are read or
    /// <see cref="IConfigurationBuilder.Build()"/> is invoked.
    /// </para>
    /// </remarks>
    /// <param name="builder">
    /// The <see cref="IConfigurationBuilder"/> to add the resolver source to.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The same <see cref="IConfigurationBuilder"/> instance, for call chaining.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a placeholder token references an undefined key, when the
    /// resolution chain contains a cycle, or when the chain exceeds the maximum
    /// allowed depth.
    /// </exception>
    public static IConfigurationBuilder AddPlaceholderResolver(this IConfigurationBuilder builder)
    {
        IConfigurationRoot root = builder is IConfigurationRoot existing ? existing : builder.Build();
        var seed = root.AsEnumerable()
                       .Where(kvp => kvp.Value is not null)
                       .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);
        builder.Add(new PlaceholderResolverSource(seed));
        return builder;
    }
}
