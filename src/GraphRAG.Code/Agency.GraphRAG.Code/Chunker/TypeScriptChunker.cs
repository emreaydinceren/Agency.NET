using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Chunks TypeScript, TSX, JavaScript, and JSX syntax trees into module, type, function, member, and fallback statement chunks.
/// </summary>
public sealed class TypeScriptChunker : IChunker
{
    private static readonly HashSet<Language> SupportedLanguages =
    [
        Language.TypeScript,
        Language.Tsx,
        Language.JavaScript,
        Language.Jsx,
    ];

    private static readonly HashSet<string> TypeDeclarationKinds =
    [
        "class_declaration",
        "abstract_class_declaration",
        "interface_declaration",
    ];

    private static readonly HashSet<string> ClassMemberKinds =
    [
        "method_definition",
        "abstract_method_signature",
    ];

    private readonly ChunkerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeScriptChunker"/> class.
    /// </summary>
    /// <param name="options">The chunker options.</param>
    public TypeScriptChunker(ChunkerOptions? options = null)
    {
        _options = options ?? new ChunkerOptions();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!SupportedLanguages.Contains(input.Language))
        {
            throw new NotSupportedException($"The {nameof(TypeScriptChunker)} only supports TypeScript-family inputs.");
        }

        object parsedRoot = await ParseAsync(input, cancellationToken).ConfigureAwait(false);
        return Chunk(input, parsedRoot);
    }

    internal IReadOnlyList<Chunk> Chunk(ChunkerInput input, object root)
    {
        AstNodeAdapter adaptedRoot = AstNodeAdapter.Create(root, input.Source);
        ChunkSourceRange range = adaptedRoot.Range ?? new ChunkSourceRange(0, 0, 0, 0);
        string moduleName = Path.GetFileNameWithoutExtension(input.Path);
        ImportReference[] imports = CollectImports(adaptedRoot);

        Chunk moduleChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Namespace,
            moduleName,
            moduleName,
            signature: null,
            input.Source,
            range,
            SymbolKind.Namespace,
            imports);

        List<Chunk> chunks = [moduleChunk];
        foreach (AstNodeAdapter child in adaptedRoot.Children)
        {
            WalkTopLevel(input, child, chunks, imports, moduleChunk.Id);
        }

        return chunks;
    }

    private void WalkTopLevel(
        ChunkerInput input,
        AstNodeAdapter node,
        List<Chunk> chunks,
        ImportReference[] imports,
        string moduleParentId)
    {
        AstNodeAdapter? declaration = UnwrapDeclaration(node);
        if (declaration is null)
        {
            return;
        }

        if (TypeDeclarationKinds.Contains(declaration.Kind))
        {
            WalkType(input, declaration, chunks, imports, moduleParentId);
            return;
        }

        if (declaration.Kind == "function_declaration")
        {
            AddTopLevelFunctionChunk(input, declaration, chunks, imports, moduleParentId);
            return;
        }

        if (declaration.Kind == "lexical_declaration")
        {
            foreach (AstNodeAdapter declarator in declaration.Children.Where(static child => child.Kind == "variable_declarator"))
            {
                AstNodeAdapter? arrowFunction = GetDirectChild(declarator, "arrow_function");
                if (arrowFunction is not null)
                {
                    AddArrowFunctionChunk(input, declaration, declarator, arrowFunction, chunks, imports, moduleParentId);
                }
            }
        }
    }

    private void WalkType(
        ChunkerInput input,
        AstNodeAdapter node,
        List<Chunk> chunks,
        ImportReference[] imports,
        string moduleParentId)
    {
        string name = GetTypeName(node);
        Chunk typeChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Type,
            name,
            name,
            BuildTypeSignature(node),
            node.Text ?? string.Empty,
            node.Range ?? throw new InvalidOperationException("Type declaration is missing a source range."),
            GetTypeSymbolKind(node.Kind),
            imports,
            moduleParentId,
            ExtractInherits(node),
            ExtractImplements(node));
        chunks.Add(typeChunk);

        AstNodeAdapter? body = GetDirectChild(node, node.Kind == "interface_declaration" ? "interface_body" : "class_body");
        if (body is null)
        {
            return;
        }

        foreach (AstNodeAdapter member in body.Children.Where(child => ClassMemberKinds.Contains(child.Kind) || child.Kind == "method_signature"))
        {
            AddTypeMemberChunk(input, member, chunks, imports, typeChunk);
        }
    }

    private void AddTopLevelFunctionChunk(
        ChunkerInput input,
        AstNodeAdapter node,
        List<Chunk> chunks,
        ImportReference[] imports,
        string moduleParentId)
    {
        string name = GetIdentifierText(node, "identifier")
            ?? throw new InvalidOperationException("Function declaration is missing an identifier.");

        Chunk functionChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Member,
            name,
            name,
            BuildFunctionSignature(node),
            node.Text ?? string.Empty,
            node.Range ?? throw new InvalidOperationException("Function declaration is missing a source range."),
            SymbolKind.Function,
            imports,
            moduleParentId);
        chunks.Add(functionChunk);

        AddStatementFallbackChunks(input, node, chunks, imports, functionChunk, SymbolKind.Function);
    }

    private void AddArrowFunctionChunk(
        ChunkerInput input,
        AstNodeAdapter declaration,
        AstNodeAdapter declarator,
        AstNodeAdapter arrowFunction,
        List<Chunk> chunks,
        ImportReference[] imports,
        string moduleParentId)
    {
        string name = GetIdentifierText(declarator, "identifier")
            ?? throw new InvalidOperationException("Arrow-function declaration is missing an identifier.");
        ChunkSourceRange range = declaration.Range ?? declarator.Range ?? throw new InvalidOperationException("Arrow-function declaration is missing a source range.");
        string signature = BuildArrowFunctionSignature(arrowFunction, name);

        Chunk functionChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Member,
            name,
            name,
            signature,
            declaration.Text ?? declarator.Text ?? string.Empty,
            range,
            SymbolKind.Function,
            imports,
            moduleParentId);
        chunks.Add(functionChunk);

        AddStatementFallbackChunks(input, arrowFunction, chunks, imports, functionChunk, SymbolKind.Function);
    }

    private void AddTypeMemberChunk(
        ChunkerInput input,
        AstNodeAdapter node,
        List<Chunk> chunks,
        ImportReference[] imports,
        Chunk parent)
    {
        string name = GetMemberName(node);
        SymbolKind symbolKind = node.Kind == "method_signature" ? SymbolKind.Method : SymbolKind.Method;

        Chunk memberChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Member,
            name,
            $"{parent.FullyQualifiedName}.{name}",
            BuildMethodSignature(node, name),
            node.Text ?? string.Empty,
            node.Range ?? throw new InvalidOperationException("Type member is missing a source range."),
            symbolKind,
            imports,
            parent.Id);
        chunks.Add(memberChunk);

        AddStatementFallbackChunks(input, node, chunks, imports, memberChunk, symbolKind);
    }

    private void AddStatementFallbackChunks(
        ChunkerInput input,
        AstNodeAdapter node,
        List<Chunk> chunks,
        ImportReference[] imports,
        Chunk parent,
        SymbolKind symbolKind)
    {
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
                ChunkBuilder.CreateStatementSymbolName(parent.FullyQualifiedName, index + 1),
                $"statement-{index + 1}",
                statement.Text ?? string.Empty,
                statement.Range,
                symbolKind,
                imports,
                parent.Id));
        }
    }

    private static IReadOnlyList<AstNodeAdapter> ExtractStatements(AstNodeAdapter node)
    {
        AstNodeAdapter? block = GetDirectChild(node, "statement_block");
        if (block is not null)
        {
            return block.Children.Where(static child => child.Kind != "{" && child.Kind != "}").ToArray();
        }

        AstNodeAdapter? arrowFunction = node.Kind == "arrow_function" ? node : GetDirectChild(node, "arrow_function");
        if (arrowFunction is null)
        {
            return [];
        }

        AstNodeAdapter? expressionBody = arrowFunction.Children.LastOrDefault(static child => child.Kind is not "formal_parameters" and not "type_annotation");
        return expressionBody is null ? [] : [expressionBody];
    }

    private static ImportReference[] CollectImports(AstNodeAdapter root)
    {
        List<ImportReference> imports = [];
        foreach (AstNodeAdapter child in root.Children)
        {
            AstNodeAdapter? declaration = UnwrapDeclaration(child);
            if (declaration?.Kind == "import_statement")
            {
                imports.AddRange(ExtractImportReferences(declaration));
                continue;
            }

            if (declaration?.Kind == "lexical_declaration")
            {
                imports.AddRange(ExtractRequireReferences(declaration));
            }
        }

        return imports.ToArray();
    }

    private static IEnumerable<ImportReference> ExtractImportReferences(AstNodeAdapter node)
    {
        string? source = ExtractStringLiteral(node);
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        AstNodeAdapter? clause = GetDirectChild(node, "import_clause");
        if (clause is null)
        {
            yield return new ImportReference(source, [], IsRelativeSource(source));
            yield break;
        }

        AstNodeAdapter? defaultImport = clause.Children.FirstOrDefault(static child => child.Kind == "identifier");
        if (defaultImport is not null)
        {
            yield return new ImportReference(source, ["default"], IsRelativeSource(source), defaultImport.Text);
        }

        AstNodeAdapter? namespaceImport = GetDirectChild(clause, "namespace_import");
        if (namespaceImport is not null)
        {
            string? alias = namespaceImport.Children.LastOrDefault(static child => child.Kind == "identifier")?.Text;
            yield return new ImportReference(source, ["*"], IsRelativeSource(source), alias);
        }

        AstNodeAdapter? namedImports = GetDirectChild(clause, "named_imports");
        if (namedImports is not null)
        {
            foreach (AstNodeAdapter specifier in namedImports.Children.Where(static child => child.Kind == "import_specifier"))
            {
                AstNodeAdapter[] identifiers = specifier.Children.Where(static child => child.Kind == "identifier").ToArray();
                if (identifiers.Length == 0)
                {
                    continue;
                }

                string importedName = identifiers[0].Text ?? string.Empty;
                string? alias = identifiers.Length > 1 ? identifiers[^1].Text : null;
                yield return new ImportReference(source, [importedName], IsRelativeSource(source), alias);
            }
        }
    }

    private static IEnumerable<ImportReference> ExtractRequireReferences(AstNodeAdapter node)
    {
        foreach (AstNodeAdapter declarator in node.Children.Where(static child => child.Kind == "variable_declarator"))
        {
            AstNodeAdapter? callExpression = GetDirectChild(declarator, "call_expression");
            if (callExpression is null || GetIdentifierText(callExpression, "identifier") != "require")
            {
                continue;
            }

            string? source = ExtractStringLiteral(callExpression);
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            string? alias = GetIdentifierText(declarator, "identifier");
            yield return new ImportReference(source, ["default"], IsRelativeSource(source), alias);
        }
    }

    private static string GetTypeName(AstNodeAdapter node)
    {
        return GetIdentifierText(node, "type_identifier")
            ?? throw new InvalidOperationException("Type declaration is missing a type identifier.");
    }

    private static string GetMemberName(AstNodeAdapter node)
    {
        return GetIdentifierText(node, "property_identifier")
            ?? GetIdentifierText(node, "identifier")
            ?? throw new InvalidOperationException("Method declaration is missing an identifier.");
    }

    private static string? BuildTypeSignature(AstNodeAdapter node)
    {
        return TrimTextBeforeKinds(node.Text);
    }

    private static string? BuildFunctionSignature(AstNodeAdapter node)
    {
        return TrimTextBeforeKinds(node.Text);
    }

    private static string BuildArrowFunctionSignature(AstNodeAdapter arrowFunction, string name)
    {
        string parameters = GetDirectChild(arrowFunction, "formal_parameters")?.Text ?? "()";
        string? typeAnnotation = GetDirectChild(arrowFunction, "type_annotation")?.Text;
        return $"const {name} = {parameters}{typeAnnotation}";
    }

    private static string? BuildMethodSignature(AstNodeAdapter node, string name)
    {
        if (node.Kind == "abstract_method_signature" || node.Kind == "method_signature")
        {
            return node.Text;
        }

        string? parameters = GetDirectChild(node, "formal_parameters")?.Text;
        string? typeAnnotation = GetDirectChild(node, "type_annotation")?.Text;
        return string.Concat(name, parameters, typeAnnotation);
    }

    private static IReadOnlyList<string> ExtractInherits(AstNodeAdapter node)
    {
        if (node.Kind == "interface_declaration")
        {
            AstNodeAdapter? extendsClause = GetDirectChild(node, "extends_type_clause");
            return extendsClause is null ? [] : ExtractTypeReferences(extendsClause);
        }

        AstNodeAdapter? heritage = GetDirectChild(node, "class_heritage");
        AstNodeAdapter? classExtends = GetDirectChild(heritage, "extends_clause");
        return classExtends is null ? [] : [TrimClauseKeyword(classExtends.Text, "extends")];
    }

    private static IReadOnlyList<string> ExtractImplements(AstNodeAdapter node)
    {
        AstNodeAdapter? heritage = GetDirectChild(node, "class_heritage");
        AstNodeAdapter? implementsClause = GetDirectChild(heritage, "implements_clause");
        return implementsClause is null ? [] : ExtractTypeReferences(implementsClause);
    }

    private static List<string> ExtractTypeReferences(AstNodeAdapter clause)
    {
        return clause.Children
            .Where(static child => child.Kind is "type_identifier" or "generic_type" or "identifier" or "member_expression")
            .Select(static child => child.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToList();
    }

    private static string? ExtractStringLiteral(AstNodeAdapter node)
    {
        AstNodeAdapter? fragment = Enumerate(node).FirstOrDefault(static child => child.Kind == "string_fragment");
        return fragment?.Text;
    }

    private static string? TrimTextBeforeKinds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        int cut = text.Length;
        foreach (string marker in new[] { "{", "=>" })
        {
            int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                cut = Math.Min(cut, markerIndex);
            }
        }

        return text[..cut].TrimEnd();
    }

    private static string TrimClauseKeyword(string? text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.StartsWith(keyword, StringComparison.Ordinal)
            ? text[(keyword.Length + 1)..].Trim()
            : text.Trim();
    }

    private static bool IsRelativeSource(string source)
    {
        return source.StartsWith(".", StringComparison.Ordinal);
    }

    private static AstNodeAdapter? UnwrapDeclaration(AstNodeAdapter node)
    {
        if (node.Kind == "export_statement")
        {
            return node.Children.FirstOrDefault(static child => child.Kind != "export" && child.Kind != "default");
        }

        return node;
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

    private static string? GetIdentifierText(AstNodeAdapter? node, string kind)
    {
        if (node is null)
        {
            return null;
        }

        return Enumerate(node).FirstOrDefault(child => child.Kind == kind)?.Text;
    }

    private static SymbolKind GetTypeSymbolKind(string kind)
    {
        return kind == "interface_declaration" ? SymbolKind.Interface : SymbolKind.Class;
    }

    private static AstNodeAdapter? GetDirectChild(AstNodeAdapter? node, string kind)
    {
        return node?.Children.FirstOrDefault(child => child.Kind == kind);
    }

    private static async Task<object> ParseAsync(ChunkerInput input, CancellationToken cancellationToken)
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
