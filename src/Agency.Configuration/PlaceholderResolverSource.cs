using Microsoft.Extensions.Configuration;

namespace Agency.Configuration;

internal sealed class PlaceholderResolverSource : IConfigurationSource
{
    private readonly IReadOnlyDictionary<string, string> _seed;

    internal PlaceholderResolverSource(IReadOnlyDictionary<string, string> seed)
    {
        _seed = seed;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new PlaceholderResolverProvider(_seed);
    }
}
