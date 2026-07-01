using Agency.Embeddings.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agency.Sql.Postgres;

/// <summary>
/// Replaces vectorize(...) calls in SQL text with generated vector literals.
/// </summary>
public sealed partial class SQLQueryEmbedder
{
    [GeneratedRegex(@"vectorize\(\s*'(?<text>(?:''|[^'])*)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex VectorizeRegex();

    private readonly IEmbeddingGenerator _embeddingGenerator;

    /// <summary>
    /// Creates a new SQL query embedder.
    /// </summary>
    public SQLQueryEmbedder(IEmbeddingGenerator embeddingGenerator)
    {
        this._embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
    }

    /// <summary>
    /// Replaces <c>vectorize(&lt;text&gt;)</c> calls with generated embedding literals.
    /// </summary>
    public async Task<string> EmbedVectorsInQueryAsync(string sqlQuery, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sqlQuery))
        {
            throw new ArgumentException("SQL query cannot be null or whitespace.", nameof(sqlQuery));
        }

        var matches = VectorizeRegex().Matches(sqlQuery);
        if (matches.Count == 0)
        {
            return sqlQuery;
        }

        var sb = new StringBuilder(sqlQuery);

        // Replace from right to left so indexes stay valid
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var inputText = match.Groups["text"].Value.Replace("''", "'");

            // Adjust type below if your generator returns a different type
            var embeddings = await this._embeddingGenerator.GenerateEmbeddingAsync(inputText, cancellationToken);

            _ = sb.Remove(match.Index, match.Length)
                .Insert(match.Index, ToSqlVectorLiteral(embeddings.Span));
        }

        return sb.ToString();
    }

    // Example format: '[0.123,-0.22,1.5]'
    // Update this method if your SQL engine expects a different vector literal format.
    private static string ToSqlVectorLiteral(ReadOnlySpan<float> vector)
    {
        var sb = new StringBuilder();

        _ = sb.Append("'[");

        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                _ = sb.Append(',');
            }
            _ = sb.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        _ = sb.Append("]'");

        return sb.ToString();
    }

}
