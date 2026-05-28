namespace Agency.Memory.Common.Records;

/// <summary>
/// A single durable memory entry stored in the vector store.
/// Represents either a <see cref="ContentType.Fact"/> (impersonal, durable) or a
/// <see cref="ContentType.Memory"/> (episodic, OAO-shaped).
/// </summary>
public sealed record Record
{
    /// <summary>Gets the surrogate primary key (UUID string).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the identifier of the user who owns this record.</summary>
    public required string UserId { get; init; }

    /// <summary>Gets the session that produced this record, or <see langword="null"/> for user-global records.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the content type discriminator.</summary>
    public required ContentType ContentType { get; init; }

    /// <summary>Gets the semantic domain (e.g., "Preferences", "Debugging").</summary>
    public required string Domain { get; init; }

    /// <summary>Gets the stable identifier within the domain (e.g., "LanguagePreference").</summary>
    public required string Key { get; init; }

    /// <summary>Gets the short human-readable title (≤ 60 chars).</summary>
    public required string Title { get; init; }

    /// <summary>Gets the Markdown body of the record.</summary>
    public required string Value { get; init; }

    /// <summary>Gets the tags associated with this record (0–5 short strings).</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>Gets the importance score in [0, 1]. Fixed at write time.</summary>
    public required double Importance { get; init; }

    /// <summary>Gets the UTC timestamp when this record was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the UTC timestamp when this record was last updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the UTC timestamp when this record was last surfaced via retrieval, or <see langword="null"/> if never retrieved.</summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>Gets the embedding vector for this record. Empty when not yet embedded.</summary>
    public ReadOnlyMemory<float> Embedding { get; init; }

    /// <summary>Gets the elapsed time since this record was last updated.</summary>
    public TimeSpan Age => DateTimeOffset.UtcNow - this.UpdatedAt;

    /// <summary>
    /// Creates a validated <see cref="Record"/> instance.
    /// </summary>
    /// <param name="id">The surrogate primary key.</param>
    /// <param name="userId">The owning user identifier.</param>
    /// <param name="sessionId">The session that produced this record, or <see langword="null"/> for global records.</param>
    /// <param name="contentType">The content type discriminator.</param>
    /// <param name="domain">The semantic domain.</param>
    /// <param name="key">The stable identifier within the domain.</param>
    /// <param name="title">The short human-readable title.</param>
    /// <param name="value">The Markdown body.</param>
    /// <param name="tags">The associated tags.</param>
    /// <param name="importance">The importance score in [0, 1].</param>
    /// <param name="createdAt">The creation timestamp.</param>
    /// <param name="updatedAt">The last-updated timestamp.</param>
    /// <param name="lastAccessedAt">The last-retrieval timestamp, or <see langword="null"/>.</param>
    /// <param name="embedding">The embedding vector, or empty if not yet embedded.</param>
    /// <returns>A new <see cref="Record"/> with validated fields.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="importance"/> is outside [0, 1].</exception>
    public static Record Create(
        string id,
        string userId,
        string? sessionId,
        ContentType contentType,
        string domain,
        string key,
        string title,
        string value,
        IReadOnlyList<string> tags,
        double importance,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? lastAccessedAt = null,
        ReadOnlyMemory<float> embedding = default)
    {
        if (importance < 0.0 || importance > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(importance), importance, "Importance must be in [0, 1].");
        }

        return new Record
        {
            Id = id,
            UserId = userId,
            SessionId = sessionId,
            ContentType = contentType,
            Domain = domain,
            Key = key,
            Title = title,
            Value = value,
            Tags = tags,
            Importance = importance,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            LastAccessedAt = lastAccessedAt,
            Embedding = embedding,
        };
    }
}
