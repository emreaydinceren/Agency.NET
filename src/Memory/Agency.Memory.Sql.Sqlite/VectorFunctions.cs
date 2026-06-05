using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite;

/// <summary>
/// Helpers for registering the cosine-distance UDF and serialising/deserialising
/// embedding vectors as JSON-array TEXT columns in SQLite.
/// </summary>
internal static class VectorFunctions
{
    /// <summary>
    /// Registers the <c>vec_distance_cosine</c> scalar function on an open SQLite connection.
    /// Returns <c>1.0 - (dot / (normA * normB))</c>, or <c>1.0</c> if either norm is zero.
    /// Must be called once per connection before issuing any similarity query.
    /// </summary>
    /// <param name="connection">The open SQLite connection to register the UDF on.</param>
    public static void RegisterVectorFunctions(SqliteConnection connection)
    {
        connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
        {
            float[] a = ParseVector(v1);
            float[] b = ParseVector(v2);
            double dot = a.Zip(b).Sum(p => (double)p.First * p.Second);
            double normA = Math.Sqrt(a.Sum(x => (double)x * x));
            double normB = Math.Sqrt(b.Sum(x => (double)x * x));
            if (normA == 0 || normB == 0)
            {
                return 1.0;
            }

            return 1.0 - (dot / (normA * normB));
        });
    }

    /// <summary>
    /// Formats a float array as a JSON-array string suitable for storage in the <c>embedding</c> column.
    /// Example: <c>[0.1,0.2,0.3]</c>.
    /// </summary>
    /// <param name="v">The embedding vector.</param>
    /// <returns>A JSON-array string.</returns>
    internal static string FormatVector(float[] v)
        => $"[{string.Join(',', v.Select(x => x.ToString(CultureInfo.InvariantCulture)))}]";

    /// <summary>
    /// Parses a JSON-array string produced by <see cref="FormatVector"/> back into a float array.
    /// </summary>
    /// <param name="raw">The raw JSON-array string.</param>
    /// <returns>The parsed float array.</returns>
    internal static float[] ParseVector(string raw)
        => raw.Trim('[', ']').Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
}