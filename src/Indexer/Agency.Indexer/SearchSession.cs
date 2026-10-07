using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agency.Indexer;

/// <summary>
/// What survives between two commands of one agent session: the hits of the last search (so <c>read --hit N</c> needs no copied
/// path) and the optional per-call search log. The cache is keyed by working directory, one small file per folder.
/// </summary>
internal static class SearchSession
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Remembers <paramref name="hits"/> as the last search of <paramref name="workingDirectory"/>; a failure to write is ignored.</summary>
    public static void Save(string home, string workingDirectory, IReadOnlyList<PrintedHit> hits)
    {
        try
        {
            string file = CacheFile(home, workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(hits, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only means `read --hit` asks for a path instead.
        }
    }

    /// <summary>The hits of the last search of <paramref name="workingDirectory"/>, or <see langword="null"/> when there was none.</summary>
    public static IReadOnlyList<PrintedHit>? Load(string home, string workingDirectory)
    {
        try
        {
            string file = CacheFile(home, workingDirectory);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<PrintedHit>>(File.ReadAllText(file), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Appends one JSON line (time, query, flags, output characters, top score) to <paramref name="path"/>.</summary>
    public static void AppendLog(string path, string query, string flags, int characters, double? topScore)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow.ToString("O"), query, flags, chars = characters, top_score = topScore }, Options);
        File.AppendAllText(full, line + Environment.NewLine);
    }

    private static string CacheFile(string home, string workingDirectory) =>
        Path.Combine(home, "last-search", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workingDirectory)))).ToLowerInvariant()[..12] + ".json");
}
