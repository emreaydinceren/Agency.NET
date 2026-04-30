namespace Agency.GraphRAG.Code.Domain;

/// <summary>
/// Classifies the syntactic or semantic role of a <see cref="Symbol"/> in the source code.
/// </summary>
public enum SymbolKind
{
    /// <summary>A namespace declaration.</summary>
    Namespace,

    /// <summary>A class declaration.</summary>
    Class,

    /// <summary>A struct declaration.</summary>
    Struct,

    /// <summary>An interface declaration.</summary>
    Interface,

    /// <summary>An enum declaration.</summary>
    Enum,

    /// <summary>A method (instance or static member function).</summary>
    Method,

    /// <summary>A free-standing or module-level function.</summary>
    Function,

    /// <summary>A property (getter/setter member).</summary>
    Property,

    /// <summary>A field (data member).</summary>
    Field,
}
