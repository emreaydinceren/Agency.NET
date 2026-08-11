namespace Agency.Memory.Retrieval;

/// <summary>
/// Builds a framing fact about retrieved memory to inject into the context knowledge,
/// informing the model about the status of memory retrieval for the current session.
/// </summary>
internal static class RetrievalMemoryFramingFact
{
    /// <summary>
    /// The constant prefix used to identify this type of fact in the knowledge facts list.
    /// </summary>
    internal const string Prefix = "Memory (retrieved): ";

    /// <summary>
    /// Builds the memory framing fact based on whether any memory records were retrieved.
    /// </summary>
    /// <param name="hasRecords">
    /// <see langword="true"/> if any memory records were retrieved or exist in memory storage;
    /// <see langword="false"/> if no records were found.
    /// </param>
    /// <returns>
    /// A framing fact string that informs the model about the memory retrieval status and
    /// how to treat retrieved memories or the lack thereof.
    /// </returns>
    internal static string Build(bool hasRecords)
    {
        if (hasRecords)
        {
            return Prefix +
                "Facts and Memories below are your own recollection from earlier sessions: " +
                "treat them as already known, use them without asking the user to restate, " +
                "and never claim you cannot remember previous conversations.";
        }

        return Prefix +
            "Nothing recalled for this user yet, which is not evidence you cannot remember " +
            "across conversations. What matters from this session is captured in the background.";
    }
}
