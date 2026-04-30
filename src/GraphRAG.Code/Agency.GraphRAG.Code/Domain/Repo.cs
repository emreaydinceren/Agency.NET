namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Represents a source code repository that has been registered for indexing.
/// </summary>
public record class Repo
{
    /// <summary>Gets the unique identifier for this repository.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the remote URL of the repository, if available.</summary>
    public string? RemoteUrl { get; init; }

    /// <summary>Gets the absolute local path where the repository is checked out.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Gets a value indicating whether the repository was cloned with a shallow history.</summary>
    public required bool IsShallow { get; init; }

    /// <summary>Gets the commit SHA that was indexed, or <c>null</c> if not yet indexed.</summary>
    public string? IndexedCommit { get; init; }

    /// <summary>Gets the timestamp of the last successful index, or <c>null</c> if not yet indexed.</summary>
    public DateTimeOffset? IndexedAt { get; init; }
}
