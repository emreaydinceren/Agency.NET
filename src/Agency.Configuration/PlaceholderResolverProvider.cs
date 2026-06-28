using Microsoft.Extensions.Configuration;

namespace Agency.Configuration;

internal sealed class PlaceholderResolverProvider : ConfigurationProvider
{
    private readonly IReadOnlyDictionary<string, string> _seed;

    internal PlaceholderResolverProvider(IReadOnlyDictionary<string, string> seed)
    {
        _seed = seed;
    }

    /// <inheritdoc/>
    public override void Load()
    {
        var expanded = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in _seed)
        {
            expanded[entry.Key] = PlaceholderExpander.Expand(entry.Value, entry.Key, _seed);
        }

        Data = expanded;
    }
}
