using System.Security.Cryptography;
using System.Text;

namespace Agency.GraphRAG.Code.Hydration;

/// <summary>
/// Generates deterministic identifiers for hydration records that do not yet exist in storage.
/// </summary>
internal static class HydrationIds
{
    /// <summary>
    /// Creates a stable <see cref="Guid"/> from the provided seed text.
    /// </summary>
    /// <param name="seed">The seed text.</param>
    /// <returns>The deterministic identifier.</returns>
    public static Guid StableGuid(string seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        byte[] guidBytes = hash[..16];
        return new Guid(guidBytes);
    }
}
