namespace Agency.Memory.Common.Records;

/// <summary>Discriminates between durable factual knowledge and episodic lived-experience memories.</summary>
public enum ContentType : short
{
    /// <summary>An impersonal, durable fact (e.g., user preference, domain constant).</summary>
    Fact = 0,

    /// <summary>An episodic memory of a past interaction, stored in OAO format.</summary>
    Memory = 1,
}
