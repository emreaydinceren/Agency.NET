using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agency.Indexer;

/// <summary>
/// The settings read from a repo-level <c>.agency-index.json</c>. The file comes with the repository, so it may only
/// describe the repository: anything else (an embeddings endpoint, a database, a key) would let a cloned repo redirect
/// document text or credentials, and is ignored.
/// </summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Values">The accepted settings as flat configuration keys (<c>Root</c> is absolute; lists are comma-joined).</param>
/// <param name="Ignored">The keys that were present but not allowed.</param>
internal sealed record RepoConfig(string Path, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Ignored)
{
    /// <summary>The only keys a repo file may set.</summary>
    public static readonly IReadOnlyList<string> AllowedKeys = ["Index", "Root", "Extensions", "Names", "MaxFileKb", "Exclude"];

    /// <summary>Reads <paramref name="path"/>; throws <see cref="UsageException"/> if it is not a JSON object.</summary>
    public static RepoConfig Load(string path)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new UsageException($"{path} must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new UsageException($"{path} is not valid JSON: {ex.Message}");
        }

        string folder = System.IO.Path.GetDirectoryName(path)!;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        try
        {
            Collect(root, folder, values, ignored);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new UsageException($"{path} has a value of the wrong type: {ex.Message}");
        }

        return new RepoConfig(path, values, ignored);
    }

    private static void Collect(JsonObject root, string folder, Dictionary<string, string> values, List<string> ignored)
    {
        foreach ((string key, JsonNode? node) in root)
        {
            string? allowed = AllowedKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (allowed is null)
            {
                ignored.Add(key);
                continue;
            }

            string? text = node switch
            {
                JsonArray array => string.Join(',', array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item))),
                JsonValue value when value.TryGetValue(out string? s) => s,
                JsonValue value when value.TryGetValue(out int n) => n.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // A relative root is relative to the file, so it means the same thing from any folder of the repo.
            values[allowed] = allowed == "Root" ? System.IO.Path.GetFullPath(text, folder) : text;
        }
    }
}
