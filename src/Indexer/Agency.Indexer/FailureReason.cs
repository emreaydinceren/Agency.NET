using System.ClientModel;

namespace Agency.Indexer;

/// <summary>Turns an exception into the one-line reason reported for a file that could not be indexed.</summary>
internal static class FailureReason
{
    private const int MaxBodyChars = 300;

    /// <summary>
    /// The messages of <paramref name="exception"/> and its inner exceptions joined with <c> -> </c>; for a failed HTTP
    /// call the status and the start of the response body are added, because the message alone ("Service request
    /// failed") does not say why the server refused.
    /// </summary>
    public static string Of(Exception exception)
    {
        var parts = new List<string>();
        Collect(exception, parts);
        return parts.Count == 0 ? exception.GetType().Name : string.Join(" -> ", parts).ReplaceLineEndings(" ");
    }

    private static void Collect(Exception exception, List<string> parts)
    {
        // An AggregateException's message repeats every inner message ("Retry failed after 4 tries. (x) (x) (x) (x)").
        string message = (exception is AggregateException ? exception.Message.Split(" (", 2)[0] : exception.Message).Trim();
        if (message.Length > 0 && !parts.Any(existing => existing.Contains(message, StringComparison.Ordinal)))
        {
            parts.Add(message);
        }

        if (exception is ClientResultException { Status: > 0 } http)
        {
            string body = http.GetRawResponse()?.Content?.ToString() ?? "";
            parts.Add($"HTTP {http.Status}{(body.Length == 0 ? "" : $": {(body.Length > MaxBodyChars ? body[..MaxBodyChars] + "..." : body)}")}");
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                Collect(inner, parts);
            }
        }
        else if (exception.InnerException is { } next)
        {
            Collect(next, parts);
        }
    }
}
