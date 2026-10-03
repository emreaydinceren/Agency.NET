namespace Agency.Indexer;

/// <summary>A user error the agent can fix by changing its arguments or configuration (exit code 2).</summary>
public sealed class UsageException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public UsageException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public UsageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the <paramref name="innerException"/> that caused it.</summary>
    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
