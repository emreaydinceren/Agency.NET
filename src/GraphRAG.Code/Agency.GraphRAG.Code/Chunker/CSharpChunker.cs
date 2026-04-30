using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Chunks C# syntax trees into namespace, type, member, and fallback statement chunks.
/// </summary>
public sealed class CSharpChunker : IChunker
{
    private static readonly HashSet<string> TypeDeclarationKinds =
    [
        "class_declaration",
        "struct_declaration",
        "interface_declaration",
        "enum_declaration",
        "record_declaration",
    ];

    private static readonly HashSet<string> MemberDeclarationKinds =
    [
        "method_declaration",
        "property_declaration",
        "field_declaration",
    ];

    private readonly ChunkerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpChunker"/> class.
    /// </summary>
    /// <param name="options">The chunker options.</param>
    public CSharpChunker(ChunkerOptions? options = null)
    {
        _options = options ?? new ChunkerOptions();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Language != Language.CSharp)
        {
            throw new NotSupportedException($"The {nameof(CSharpChunker)} only supports C# inputs.");
        }

        object parsedRoot = await ParseCSharpAsync(input, cancellationToken).ConfigureAwait(false);
        return Chunk(input, parsedRoot);
    }

    internal IReadOnlyList<Chunk> Chunk(ChunkerInput input, object root)
    {
        AstNodeAdapter adaptedRoot = AstNodeAdapter.Create(root, input.Source);
        Dictionary<string, SymbolKind> declaredTypes = CollectDeclaredTypes(adaptedRoot);
        List<Chunk> chunks = [];
        WalkCompilationUnit(input, adaptedRoot, declaredTypes, chunks, [], []);
        return chunks;
    }

    private static Dictionary<string, SymbolKind> CollectDeclaredTypes(AstNodeAdapter root)
    {
        Dictionary<string, SymbolKind> map = new(StringComparer.Ordinal);
        foreach (AstNodeAdapter node in Enumerate(root))
        {
            if (!TypeDeclarationKinds.Contains(node.Kind))
            {
                continue;
            }

            string? name = GetDirectChild(node, "identifier")?.Text;
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[name] = GetSymbolKind(node.Kind);
            }
        }

        return map;
    }

    private static IEnumerable<AstNodeAdapter> Enumerate(AstNodeAdapter root)
    {
        yield return root;
        foreach (AstNodeAdapter child in root.Children)
        {
            foreach (AstNodeAdapter descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }

    private void WalkCompilationUnit(
        ChunkerInput input,
        AstNodeAdapter node,
        IReadOnlyDictionary<string, SymbolKind> declaredTypes,
        List<Chunk> chunks,
        IReadOnlyList<string> namespaceSegments,
        IReadOnlyList<string> usingsInScope)
    {
        List<string> currentUsings = [.. usingsInScope];
        string[] currentNamespaceSegments = [.. namespaceSegments];
        string? currentNamespaceParentId = null;

        foreach (AstNodeAdapter child in node.Children.Where(static child => child.Kind == "using_directive"))
        {
            string usingText = ExtractUsingText(child);
            if (!string.IsNullOrWhiteSpace(usingText) && !currentUsings.Contains(usingText, StringComparer.Ordinal))
            {
                currentUsings.Add(usingText);
            }
        }

        foreach (AstNodeAdapter child in node.Children)
        {
            switch (child.Kind)
            {
                case "using_directive":
                    break;
                case "file_scoped_namespace_declaration":
                    (Chunk namespaceChunk, string[] fileScopedNamespaceSegments) = BuildNamespaceChunk(input, child, currentNamespaceSegments, currentUsings);
                    chunks.Add(namespaceChunk);
                    currentNamespaceSegments = fileScopedNamespaceSegments;
                    currentNamespaceParentId = namespaceChunk.Id;
                    break;
                case "namespace_declaration":
                    WalkNamespace(input, child, declaredTypes, chunks, currentNamespaceSegments, currentUsings);
                    break;
                default:
                    if (TypeDeclarationKinds.Contains(child.Kind))
                    {
                        WalkType(input, child, declaredTypes, chunks, currentNamespaceSegments, currentUsings, currentNamespaceParentId);
                    }

                    break;
            }
        }
    }

    private void WalkNamespace(
        ChunkerInput input,
        AstNodeAdapter node,
        IReadOnlyDictionary<string, SymbolKind> declaredTypes,
        List<Chunk> chunks,
        IReadOnlyList<string> parentNamespaceSegments,
        IReadOnlyList<string> inheritedUsings)
    {
        (Chunk namespaceChunk, string[] namespaceSegments) = BuildNamespaceChunk(input, node, parentNamespaceSegments, inheritedUsings);
        chunks.Add(namespaceChunk);

        AstNodeAdapter? declarationList = GetDirectChild(node, "declaration_list");
        if (declarationList is null)
        {
            return;
        }

        List<string> namespaceUsings = [.. inheritedUsings];
        foreach (AstNodeAdapter usingNode in declarationList.Children.Where(static child => child.Kind == "using_directive"))
        {
            string usingText = ExtractUsingText(usingNode);
            if (!string.IsNullOrWhiteSpace(usingText) && !namespaceUsings.Contains(usingText, StringComparer.Ordinal))
            {
                namespaceUsings.Add(usingText);
            }
        }

        foreach (AstNodeAdapter child in declarationList.Children)
        {
            switch (child.Kind)
            {
                case "using_directive":
                    break;
                case "namespace_declaration":
                    WalkNamespace(input, child, declaredTypes, chunks, namespaceSegments, namespaceUsings);
                    break;
                default:
                    if (TypeDeclarationKinds.Contains(child.Kind))
                    {
                        WalkType(input, child, declaredTypes, chunks, namespaceSegments, namespaceUsings, namespaceChunk.Id);
                    }

                    break;
            }
        }
    }

    private static (Chunk Chunk, string[] NamespaceSegments) BuildNamespaceChunk(
        ChunkerInput input,
        AstNodeAdapter node,
        IReadOnlyList<string> parentNamespaceSegments,
        IReadOnlyList<string> inheritedUsings)
    {
        AstNodeAdapter? nameNode = node.Children.FirstOrDefault(static child => child.Kind is "qualified_name" or "identifier");
        string name = nameNode?.Text ?? string.Empty;
        string[] localSegments = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] namespaceSegments = [.. parentNamespaceSegments, .. localSegments];
        string fullyQualifiedName = string.Join(".", namespaceSegments);
        ChunkSourceRange range = node.Range ?? throw new InvalidOperationException("Namespace node is missing a source range.");

        Chunk namespaceChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Namespace,
            localSegments.LastOrDefault() ?? name,
            fullyQualifiedName,
            signature: null,
            content: node.Text ?? string.Empty,
            range,
            SymbolKind.Namespace,
            CreateImportReferences(inheritedUsings));
        return (namespaceChunk, namespaceSegments);
    }

    private void WalkType(
        ChunkerInput input,
        AstNodeAdapter node,
        IReadOnlyDictionary<string, SymbolKind> declaredTypes,
        List<Chunk> chunks,
        IReadOnlyList<string> namespaceSegments,
        IReadOnlyList<string> usingsInScope,
        string? parentId)
    {
        string name = GetDirectChild(node, "identifier")?.Text
            ?? throw new InvalidOperationException($"Type declaration '{node.Kind}' is missing an identifier.");
        string fullyQualifiedName = namespaceSegments.Count == 0
            ? name
            : $"{string.Join(".", namespaceSegments)}.{name}";

        string? signature = BuildTypeSignature(
            name,
            GetDirectChild(node, "type_parameter_list"),
            node.Children.FirstOrDefault(static child => child.Kind == "base_list"),
            GetDirectChildren(node, "type_parameter_constraints_clause"));
        (IReadOnlyList<string> inherits, IReadOnlyList<string> implements) = ExtractBaseRelationships(node, declaredTypes);
        ChunkSourceRange range = node.Range ?? throw new InvalidOperationException("Type node is missing a source range.");

        Chunk typeChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Type,
            name,
            fullyQualifiedName,
            signature,
            node.Text ?? string.Empty,
            range,
            GetSymbolKind(node.Kind),
            CreateImportReferences(usingsInScope),
            parentId,
            inherits,
            implements);
        chunks.Add(typeChunk);

        if (node.Kind == "record_declaration")
        {
            AstNodeAdapter? parameterList = GetDirectChild(node, "parameter_list");
            if (parameterList is not null && parameterList.Range is not null)
            {
                string constructorFqn = $"{fullyQualifiedName}.{name}";
                chunks.Add(ChunkBuilder.Build(
                    input.Path,
                    input.Language,
                    ChunkGranularity.Member,
                    name,
                    constructorFqn,
                    $"{name}{parameterList.Text}",
                    parameterList.Text ?? string.Empty,
                    parameterList.Range,
                    SymbolKind.Method,
                    CreateImportReferences(usingsInScope),
                    typeChunk.Id));
            }
        }

        AstNodeAdapter? declarationList = GetDirectChild(node, "declaration_list");
        if (declarationList is null)
        {
            return;
        }

        foreach (AstNodeAdapter member in declarationList.Children.Where(static child => MemberDeclarationKinds.Contains(child.Kind)))
        {
            WalkMember(input, member, chunks, usingsInScope, fullyQualifiedName, typeChunk.Id);
        }
    }

    private void WalkMember(
        ChunkerInput input,
        AstNodeAdapter node,
        List<Chunk> chunks,
        IReadOnlyList<string> usingsInScope,
        string containingTypeName,
        string parentId)
    {
        string name = GetMemberName(node);
        string fullyQualifiedName = $"{containingTypeName}.{name}";
        string? signature = BuildMemberSignature(node, name);
        ChunkSourceRange range = node.Range ?? throw new InvalidOperationException("Member node is missing a source range.");
        SymbolKind symbolKind = GetMemberSymbolKind(node.Kind);

        Chunk memberChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Member,
            name,
            fullyQualifiedName,
            signature,
            node.Text ?? string.Empty,
            range,
            symbolKind,
            CreateImportReferences(usingsInScope),
            parentId);
        chunks.Add(memberChunk);

        if ((node.Text?.Length ?? 0) <= _options.MaxChunkChars)
        {
            return;
        }

        IReadOnlyList<AstNodeAdapter> statements = ExtractStatements(node);
        for (int index = 0; index < statements.Count; index++)
        {
            AstNodeAdapter statement = statements[index];
            if (statement.Range is null)
            {
                continue;
            }

            chunks.Add(ChunkBuilder.Build(
                input.Path,
                input.Language,
                ChunkGranularity.Statement,
                $"statement#{index + 1}",
                ChunkBuilder.CreateStatementSymbolName(memberChunk.FullyQualifiedName, index + 1),
                $"statement-{index + 1}",
                statement.Text ?? string.Empty,
                statement.Range,
                symbolKind,
                CreateImportReferences(usingsInScope),
                memberChunk.Id));
        }
    }

    private static IReadOnlyList<AstNodeAdapter> ExtractStatements(AstNodeAdapter node)
    {
        AstNodeAdapter? block = GetDirectChild(node, "block");
        if (block is not null)
        {
            return block.Children;
        }

        AstNodeAdapter? arrowExpression = GetDirectChild(node, "arrow_expression_clause");
        return arrowExpression is null ? [] : arrowExpression.Children;
    }

    private static string ExtractUsingText(AstNodeAdapter node)
    {
        AstNodeAdapter? target = node.Children.FirstOrDefault(static child => child.Kind is "qualified_name" or "identifier");
        return target?.Text ?? string.Empty;
    }

    private static string GetMemberName(AstNodeAdapter node)
    {
        if (node.Kind == "field_declaration")
        {
            AstNodeAdapter? variableDeclarator = Enumerate(node).FirstOrDefault(static child => child.Kind == "variable_declarator");
            string? fieldName = GetDirectChild(variableDeclarator, "identifier")?.Text;
            return fieldName ?? throw new InvalidOperationException("Field declaration is missing an identifier.");
        }

        return node.Children.LastOrDefault(child => child.Kind == "identifier")?.Text
            ?? throw new InvalidOperationException($"Member declaration '{node.Kind}' is missing an identifier.");
    }

    private static SymbolKind GetSymbolKind(string nodeKind)
    {
        return nodeKind switch
        {
            "class_declaration" => SymbolKind.Class,
            "record_declaration" => SymbolKind.Class,
            "struct_declaration" => SymbolKind.Struct,
            "interface_declaration" => SymbolKind.Interface,
            "enum_declaration" => SymbolKind.Enum,
            _ => throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unsupported type declaration."),
        };
    }

    private static SymbolKind GetMemberSymbolKind(string nodeKind)
    {
        return nodeKind switch
        {
            "method_declaration" => SymbolKind.Method,
            "property_declaration" => SymbolKind.Property,
            "field_declaration" => SymbolKind.Field,
            _ => throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unsupported member declaration."),
        };
    }

    private static string? BuildTypeSignature(
        string name,
        AstNodeAdapter? typeParameters,
        AstNodeAdapter? baseList,
        IEnumerable<AstNodeAdapter> constraints)
    {
        List<string> parts = [name];
        if (!string.IsNullOrWhiteSpace(typeParameters?.Text))
        {
            parts[0] += typeParameters!.Text;
        }

        if (!string.IsNullOrWhiteSpace(baseList?.Text))
        {
            parts.Add(baseList!.Text);
        }

        foreach (AstNodeAdapter constraint in constraints)
        {
            if (!string.IsNullOrWhiteSpace(constraint.Text))
            {
                parts.Add(constraint.Text!);
            }
        }

        return string.Join(" ", parts);
    }

    private static string? BuildMemberSignature(AstNodeAdapter node, string name)
    {
        if (node.Kind == "field_declaration")
        {
            return GetDirectChild(node, "variable_declaration")?.Text;
        }

        List<string> parts = [];
        AstNodeAdapter? returnType = node.Children.FirstOrDefault(static child => child.Kind.EndsWith("_type", StringComparison.Ordinal) || child.Kind is "identifier" or "generic_name" or "qualified_name" or "nullable_type" or "tuple_type");
        if (node.Kind == "method_declaration" && !string.IsNullOrWhiteSpace(returnType?.Text))
        {
            parts.Add(returnType!.Text!);
        }

        string memberName = name;
        AstNodeAdapter? typeParameters = GetDirectChild(node, "type_parameter_list");
        if (!string.IsNullOrWhiteSpace(typeParameters?.Text))
        {
            memberName += typeParameters!.Text;
        }

        parts.Add(memberName);

        AstNodeAdapter? parameterList = GetDirectChild(node, "parameter_list");
        if (!string.IsNullOrWhiteSpace(parameterList?.Text))
        {
            parts.Add(parameterList!.Text!);
        }

        foreach (AstNodeAdapter constraint in GetDirectChildren(node, "type_parameter_constraints_clause"))
        {
            if (!string.IsNullOrWhiteSpace(constraint.Text))
            {
                parts.Add(constraint.Text!);
            }
        }

        return string.Join(" ", parts);
    }

    private static (IReadOnlyList<string> Inherits, IReadOnlyList<string> Implements) ExtractBaseRelationships(
        AstNodeAdapter node,
        IReadOnlyDictionary<string, SymbolKind> declaredTypes)
    {
        AstNodeAdapter? baseList = GetDirectChild(node, "base_list");
        if (baseList is null)
        {
            return ([], []);
        }

        List<string> baseTypes = baseList.Children
            .Select(static child => child.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text) && text is not ":" and not ",")
            .Cast<string>()
            .ToList();
        if (baseTypes.Count == 0)
        {
            return ([], []);
        }

        if (node.Kind == "interface_declaration")
        {
            return (baseTypes, []);
        }

        int inheritedIndex = -1;
        for (int index = 0; index < baseTypes.Count; index++)
        {
            string baseTypeName = SimplifyTypeName(baseTypes[index]);
            if (declaredTypes.TryGetValue(baseTypeName, out SymbolKind kind) && kind is SymbolKind.Class or SymbolKind.Struct)
            {
                inheritedIndex = index;
                break;
            }

            if (!LooksLikeInterface(baseTypeName))
            {
                inheritedIndex = index;
                break;
            }
        }

        if (inheritedIndex < 0)
        {
            return ([], baseTypes);
        }

        List<string> inherits = [baseTypes[inheritedIndex]];
        List<string> implements = [];
        for (int index = 0; index < baseTypes.Count; index++)
        {
            if (index != inheritedIndex)
            {
                implements.Add(baseTypes[index]);
            }
        }

        return (inherits, implements);
    }

    private static bool LooksLikeInterface(string typeName)
    {
        return typeName.Length > 1
            && typeName[0] == 'I'
            && char.IsUpper(typeName[1]);
    }

    private static string SimplifyTypeName(string typeName)
    {
        int genericIndex = typeName.IndexOf('<', StringComparison.Ordinal);
        string simplified = genericIndex >= 0 ? typeName[..genericIndex] : typeName;
        int qualifiedIndex = simplified.LastIndexOf(".");
        return qualifiedIndex >= 0 ? simplified[(qualifiedIndex + 1)..] : simplified;
    }

    private static AstNodeAdapter? GetDirectChild(AstNodeAdapter? node, string kind)
    {
        return node?.Children.FirstOrDefault(child => child.Kind == kind);
    }

    private static IEnumerable<AstNodeAdapter> GetDirectChildren(AstNodeAdapter node, string kind)
    {
        return node.Children.Where(child => child.Kind == kind);
    }

    private static ImportReference[] CreateImportReferences(IReadOnlyList<string> usingsInScope)
    {
        return usingsInScope
            .Select(@using => new ImportReference(@using, [], IsRelative: @using.StartsWith(".", StringComparison.Ordinal)))
            .ToArray();
    }

    private static async Task<object> ParseCSharpAsync(ChunkerInput input, CancellationToken cancellationToken)
    {
        Type? clientType = Type.GetType(
            "Agency.GraphRAG.Code.TreeSitter.TreeSitterClient, Agency.GraphRAG.Code.TreeSitter",
            throwOnError: false);
        if (clientType is null)
        {
            throw new InvalidOperationException("Tree-sitter client is unavailable. Ensure the tree-sitter project is loaded.");
        }

        object client = Activator.CreateInstance(clientType, "node", null)
            ?? throw new InvalidOperationException("Failed to create the tree-sitter client.");

        try
        {
            object taskObject = clientType
                .GetMethod("ParseAsync", [typeof(string), typeof(Language), typeof(string), typeof(CancellationToken)])
                ?.Invoke(client, [input.Path, input.Language, input.Source, cancellationToken])
                ?? throw new InvalidOperationException("Tree-sitter ParseAsync could not be invoked.");

            dynamic parsed = await (dynamic)taskObject;
            return parsed.Root;
        }
        finally
        {
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed record AstNodeAdapter(string Kind, string? Text, ChunkSourceRange? Range, IReadOnlyList<AstNodeAdapter> Children)
    {
        public static AstNodeAdapter Create(object node, string source)
        {
            ArgumentNullException.ThrowIfNull(node);
            ArgumentNullException.ThrowIfNull(source);

            Type type = node.GetType();
            string kind = (string?)type.GetProperty("Kind")?.GetValue(node)
                ?? throw new InvalidOperationException("AST node is missing a Kind property.");
            object? rangeValue = type.GetProperty("Range")?.GetValue(node);
            ChunkSourceRange? range = rangeValue is null ? null : CreateRange(rangeValue);
            string? text = (string?)type.GetProperty("Text")?.GetValue(node) ?? ExtractText(source, range);
            IEnumerable<object> children = ((System.Collections.IEnumerable?)type.GetProperty("Children")?.GetValue(node))
                ?.Cast<object>()
                ?? throw new InvalidOperationException("AST node is missing a Children property.");

            return new AstNodeAdapter(kind, text, range, children.Select(child => Create(child, source)).ToArray());
        }

        private static ChunkSourceRange CreateRange(object range)
        {
            Type type = range.GetType();
            return new ChunkSourceRange(
                (int)(type.GetProperty("StartLine")?.GetValue(range) ?? 0),
                (int)(type.GetProperty("StartColumn")?.GetValue(range) ?? 0),
                (int)(type.GetProperty("EndLine")?.GetValue(range) ?? 0),
                (int)(type.GetProperty("EndColumn")?.GetValue(range) ?? 0));
        }

        private static string? ExtractText(string source, ChunkSourceRange? range)
        {
            if (range is null)
            {
                return null;
            }

            int start = GetOffset(source, range.StartLine, range.StartColumn);
            int end = GetOffset(source, range.EndLine, range.EndColumn);
            return start >= 0 && end >= start && end <= source.Length
                ? source[start..end]
                : null;
        }

        private static int GetOffset(string source, int targetLine, int targetColumn)
        {
            int line = 0;
            int column = 0;

            for (int index = 0; index < source.Length; index++)
            {
                if (line == targetLine && column == targetColumn)
                {
                    return index;
                }

                if (source[index] == '\n')
                {
                    line++;
                    column = 0;
                }
                else
                {
                    column++;
                }
            }

            return line == targetLine && column == targetColumn ? source.Length : -1;
        }
    }
}
