namespace Agency.Memory.Distiller.Prompts;

/// <summary>
/// Thrown by <see cref="EpisodeExtractionParser"/> when the LLM response cannot be parsed
/// as valid JSON or does not conform to the expected record schema.
/// </summary>
/// <remarks>
/// This exception class is used by the Distiller retry loop to distinguish permanent parse
/// failures from transient LLM errors (Spec §8.6).
/// </remarks>
// S3871 (exception types should be public): deliberately internal — a control-flow signal for
// the Distiller retry loop only (see remarks above), not a type external callers should catch by name.
#pragma warning disable S3871
internal sealed class ExtractionParseException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="ExtractionParseException"/> with the given message.
    /// </summary>
    /// <param name="message">Human-readable description of the parse failure.</param>
    internal ExtractionParseException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="ExtractionParseException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Human-readable description of the parse failure.</param>
    /// <param name="innerException">The underlying exception (e.g., a <see cref="System.Text.Json.JsonException"/>).</param>
    internal ExtractionParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
#pragma warning restore S3871
