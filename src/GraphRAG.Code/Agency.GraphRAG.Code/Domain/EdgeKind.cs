namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Describes the semantic relationship type of an <see cref="Edge"/> between two graph nodes.
/// </summary>
public enum EdgeKind
{
    /// <summary>The source node contains the target node (e.g. a module contains a symbol).</summary>
    Contains,

    /// <summary>The source node depends on the target node at the package or project level.</summary>
    DependsOn,

    /// <summary>The source file imports or uses the target module or namespace.</summary>
    Imports,

    /// <summary>The source symbol references the target symbol.</summary>
    References,

    /// <summary>The source module or file defines the target symbol.</summary>
    Defines,

    /// <summary>The source symbol is a member of the target module or class.</summary>
    MemberOf,
}
