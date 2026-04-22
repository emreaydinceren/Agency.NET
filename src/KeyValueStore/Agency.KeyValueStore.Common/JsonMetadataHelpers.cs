namespace Agency.KeyValueStore.Common;

using System.Text.Json;

/// <summary>
/// Shared helpers for deserializing JSON metadata stored in key-value store rows.
/// </summary>
public static class JsonMetadataHelpers
{
    /// <summary>
    /// Deserializes a JSON string into a metadata dictionary, converting
    /// <see cref="JsonElement"/> leaf values to their native CLR types.
    /// </summary>
    /// <param name="metadataJson">Raw JSON text, or <see langword="null"/>.</param>
    /// <returns>A populated dictionary, or <see langword="null"/> when the input is empty.</returns>
    public static Dictionary<string, object>? DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
        if (raw == null)
        {
            return null;
        }

        return raw.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value is JsonElement element ? ConvertJsonElementToObject(element) : kvp.Value);
    }

    /// <summary>
    /// Recursively converts a <see cref="JsonElement"/> to its native CLR equivalent:
    /// <see langword="string"/>, <see langword="long"/>, <see langword="double"/>,
    /// <see langword="decimal"/>, <see langword="bool"/>, <see langword="null"/>,
    /// <c>Dictionary&lt;string, object&gt;</c>, or <c>List&lt;object&gt;</c>.
    /// </summary>
    /// <param name="element">The element to convert.</param>
    public static object ConvertJsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out long i)
                ? i
                : element.TryGetDouble(out double d)
                    ? d
                    : (object)element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElementToObject(p.Value)!),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElementToObject)
                .ToList(),
            _ => element.GetRawText()
        };
    }
}