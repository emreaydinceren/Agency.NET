using Agency.GraphRAG.Code.Chunker.Internal.Python;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Chunker;

/// <summary>
/// Chunks Python syntax trees into module, type, function, and fallback statement chunks.
/// </summary>
public sealed class PythonChunker : IChunker
{
    private static object? s_sharedTreeSitterClient;
    private static readonly object s_clientLock = new();

    private readonly ChunkerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="PythonChunker"/> class.
    /// </summary>
    /// <param name="options">The chunker options.</param>
    public PythonChunker(ChunkerOptions? options = null)
    {
        _options = options ?? new ChunkerOptions();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chunk>> ChunkAsync(ChunkerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Language != Language.Python)
        {
            throw new NotSupportedException($"The {nameof(PythonChunker)} only supports Python inputs.");
        }

        try
        {
            object parsedRoot = await ParsePythonAsync(input, cancellationToken).ConfigureAwait(false);
            return Chunk(input, parsedRoot);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            string fileName = System.IO.Path.GetFileName(input.Path);
            return [new Chunk(
                Id: ChunkBuilder.CreateStableId(input.Path, input.Path, ""),
                Path: input.Path,
                Language: input.Language,
                Granularity: ChunkGranularity.Namespace,
                Name: fileName,
                FullyQualifiedName: input.Path,
                Signature: null,
                Content: input.Source,
                Range: new ChunkSourceRange(0, 0, 0, 0),
                SymbolKind: SymbolKind.Namespace,
                ImportsInScope: [])];
        }
    }

    internal IReadOnlyList<Chunk> Chunk(ChunkerInput input, object root)
    {
        AstNodeAdapter adaptedRoot = AstNodeAdapter.Create(root, input.Source);
        PythonImportMap importMap = PythonImportMap.Create(adaptedRoot);
        Dictionary<string, SymbolKind> declaredTypes = CollectDeclaredTypes(adaptedRoot, importMap);
        List<Chunk> chunks = [];

        Chunk moduleChunk = BuildModuleChunk(input, adaptedRoot, importMap.Imports);
        chunks.Add(moduleChunk);

        foreach (AstNodeAdapter child in adaptedRoot.Children)
        {
            if (TryGetClassNode(child, out AstNodeAdapter? classNode))
            {
                WalkClass(input, classNode!, child, declaredTypes, importMap, chunks, moduleChunk);
                continue;
            }

            if (TryGetFunctionNode(child, out AstNodeAdapter? functionNode))
            {
                WalkFunction(input, functionNode!, child, importMap.Imports, chunks, moduleChunk.FullyQualifiedName, moduleChunk.Id, isTopLevel: true);
            }
        }

        return chunks;
    }

    private static Dictionary<string, SymbolKind> CollectDeclaredTypes(AstNodeAdapter root, PythonImportMap importMap)
    {
        Dictionary<string, SymbolKind> map = new(StringComparer.Ordinal);
        foreach (AstNodeAdapter node in Enumerate(root))
        {
            if (!TryGetClassNode(node, out AstNodeAdapter? classNode))
            {
                continue;
            }

            string? name = GetName(classNode!);
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[name] = IsInterface(classNode!, importMap) ? SymbolKind.Interface : SymbolKind.Class;
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

    private static Chunk BuildModuleChunk(ChunkerInput input, AstNodeAdapter root, IReadOnlyList<ImportReference> imports)
    {
        string moduleName = BuildModuleName(input.Path);
        ChunkSourceRange range = root.Range ?? CreateModuleRange(input.Source);
        return ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Namespace,
            Path.GetFileNameWithoutExtension(input.Path),
            moduleName,
            signature: null,
            input.Source,
            range,
            SymbolKind.Namespace,
            imports);
    }

    private void WalkClass(
        ChunkerInput input,
        AstNodeAdapter classNode,
        AstNodeAdapter contentNode,
        IReadOnlyDictionary<string, SymbolKind> declaredTypes,
        PythonImportMap importMap,
        List<Chunk> chunks,
        Chunk moduleChunk)
    {
        string name = GetName(classNode) ?? throw new InvalidOperationException("Python class is missing a name.");
        string fullyQualifiedName = $"{moduleChunk.FullyQualifiedName}.{name}";
        ChunkSourceRange range = contentNode.Range ?? throw new InvalidOperationException("Python class node is missing a range.");
        (IReadOnlyList<string> inherits, IReadOnlyList<string> implements) = ExtractBaseRelationships(classNode, declaredTypes, importMap);

        Chunk typeChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Type,
            name,
            fullyQualifiedName,
            BuildSignature(classNode.Text),
            contentNode.Text ?? classNode.Text ?? string.Empty,
            range,
            GetClassSymbolKind(classNode, importMap),
            importMap.Imports,
            moduleChunk.Id,
            inherits,
            implements);
        chunks.Add(typeChunk);

        AstNodeAdapter? body = classNode.Children.FirstOrDefault(static child => child.FieldName == "body");
        if (body is null)
        {
            return;
        }

        foreach (AstNodeAdapter child in body.Children)
        {
            if (TryGetFunctionNode(child, out AstNodeAdapter? functionNode))
            {
                WalkFunction(input, functionNode!, child, importMap.Imports, chunks, fullyQualifiedName, typeChunk.Id, isTopLevel: false);
            }
        }
    }

    private void WalkFunction(
        ChunkerInput input,
        AstNodeAdapter functionNode,
        AstNodeAdapter contentNode,
        IReadOnlyList<ImportReference> imports,
        List<Chunk> chunks,
        string containingName,
        string parentId,
        bool isTopLevel)
    {
        string name = GetName(functionNode) ?? throw new InvalidOperationException("Python function is missing a name.");
        string fullyQualifiedName = $"{containingName}.{name}";
        ChunkSourceRange range = contentNode.Range ?? throw new InvalidOperationException("Python function node is missing a range.");
        SymbolKind symbolKind = isTopLevel ? SymbolKind.Function : SymbolKind.Method;

        Chunk functionChunk = ChunkBuilder.Build(
            input.Path,
            input.Language,
            ChunkGranularity.Member,
            name,
            fullyQualifiedName,
            BuildSignature(functionNode.Text),
            contentNode.Text ?? functionNode.Text ?? string.Empty,
            range,
            symbolKind,
            imports,
            parentId);
        chunks.Add(functionChunk);

        int contentLength = (contentNode.Text ?? functionNode.Text ?? string.Empty).Length;
        if (contentLength <= _options.MaxChunkChars)
        {
            return;
        }

        AstNodeAdapter? body = functionNode.Children.FirstOrDefault(static child => child.FieldName == "body");
        if (body is null)
        {
            return;
        }

        int index = 0;
        foreach (AstNodeAdapter statement in body.Children)
        {
            if (statement.Range is null || string.IsNullOrWhiteSpace(statement.Text))
            {
                continue;
            }

            index++;
            chunks.Add(ChunkBuilder.Build(
                input.Path,
                input.Language,
                ChunkGranularity.Statement,
                $"statement#{index}",
                ChunkBuilder.CreateStatementSymbolName(functionChunk.FullyQualifiedName, index),
                $"statement-{index}",
                statement.Text!,
                statement.Range,
                symbolKind,
                imports,
                functionChunk.Id));
        }
    }

    private static bool TryGetClassNode(AstNodeAdapter node, out AstNodeAdapter? classNode)
    {
        if (node.Kind == "class_definition")
        {
            classNode = node;
            return true;
        }

        if (node.Kind == "decorated_definition")
        {
            classNode = node.Children.FirstOrDefault(static child => child.Kind == "class_definition" || child.FieldName == "definition" && child.Kind == "class_definition");
            return classNode is not null;
        }

        classNode = null;
        return false;
    }

    private static bool TryGetFunctionNode(AstNodeAdapter node, out AstNodeAdapter? functionNode)
    {
        if (node.Kind == "function_definition")
        {
            functionNode = node;
            return true;
        }

        if (node.Kind == "decorated_definition")
        {
            functionNode = node.Children.FirstOrDefault(static child => child.Kind == "function_definition" || child.FieldName == "definition" && child.Kind == "function_definition");
            return functionNode is not null;
        }

        functionNode = null;
        return false;
    }

    private static string? GetName(AstNodeAdapter node)
    {
        return node.Children.FirstOrDefault(static child => child.FieldName == "name")?.Text;
    }

    private static SymbolKind GetClassSymbolKind(AstNodeAdapter classNode, PythonImportMap importMap)
    {
        return IsInterface(classNode, importMap) ? SymbolKind.Interface : SymbolKind.Class;
    }

    private static bool IsInterface(AstNodeAdapter classNode, PythonImportMap importMap)
    {
        foreach (string baseType in GetBaseTypes(classNode))
        {
            string resolved = importMap.ResolveQualifiedName(baseType);
            if (string.Equals(resolved, "abc.ABC", StringComparison.Ordinal)
                || string.Equals(resolved, "typing.Protocol", StringComparison.Ordinal)
                || string.Equals(resolved, "typing_extensions.Protocol", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static (IReadOnlyList<string> Inherits, IReadOnlyList<string> Implements) ExtractBaseRelationships(
        AstNodeAdapter classNode,
        IReadOnlyDictionary<string, SymbolKind> declaredTypes,
        PythonImportMap importMap)
    {
        List<string> inherits = [];
        List<string> implements = [];

        foreach (string baseType in GetBaseTypes(classNode))
        {
            string resolved = importMap.ResolveQualifiedName(baseType);
            if (string.Equals(resolved, "abc.ABC", StringComparison.Ordinal)
                || string.Equals(resolved, "typing.Protocol", StringComparison.Ordinal)
                || string.Equals(resolved, "typing_extensions.Protocol", StringComparison.Ordinal))
            {
                continue;
            }

            string displayName = SimplifyTypeName(baseType);
            string lookupName = SimplifyTypeName(resolved);
            if (declaredTypes.TryGetValue(lookupName, out SymbolKind kind) && kind == SymbolKind.Interface)
            {
                implements.Add(displayName);
            }
            else
            {
                inherits.Add(displayName);
            }
        }

        return (inherits, implements);
    }

    private static IReadOnlyList<string> GetBaseTypes(AstNodeAdapter classNode)
    {
        AstNodeAdapter? baseList = classNode.Children.FirstOrDefault(static child => child.FieldName == "superclasses");
        if (baseList is null)
        {
            return [];
        }

        return baseList.Children
            .Where(static child => child.Kind is not "(" and not ")" and not ",")
            .Select(static child => child.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToArray();
    }

    private static string SimplifyTypeName(string typeName)
    {
        string simplified = typeName.Trim();
        int genericIndex = simplified.IndexOf('[', StringComparison.Ordinal);
        if (genericIndex >= 0)
        {
            simplified = simplified[..genericIndex];
        }

        int dotIndex = simplified.LastIndexOf('.');
        return dotIndex >= 0 ? simplified[(dotIndex + 1)..] : simplified;
    }

    private static string? BuildSignature(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.EndsWith(":", StringComparison.Ordinal) ? firstLine[..^1] : firstLine;
    }

    private static string BuildModuleName(string path)
    {
        string normalized = Path.ChangeExtension(path.Replace('/', '\\'), null) ?? path;
        string moduleName = normalized.Replace('\\', '.');
        return moduleName.EndsWith(".__init__", StringComparison.Ordinal)
            ? moduleName[..^9]
            : moduleName;
    }

    private static ChunkSourceRange CreateModuleRange(string source)
    {
        if (source.Length == 0)
        {
            return new ChunkSourceRange(0, 0, 0, 0);
        }

        string[] lines = source.Split('\n');
        int endLine = lines.Length - 1;
        int endColumn = lines[^1].Length;
        if (source.EndsWith("\n", StringComparison.Ordinal))
        {
            endLine++;
            endColumn = 0;
        }

        return new ChunkSourceRange(0, 0, endLine, endColumn);
    }

    private static async Task<object> ParsePythonAsync(ChunkerInput input, CancellationToken cancellationToken)
    {
        if (s_sharedTreeSitterClient is null)
        {
            lock (s_clientLock)
            {
                s_sharedTreeSitterClient ??= CreateSharedClient();
            }
        }

        return await ParseWithClientAsync(s_sharedTreeSitterClient, input, cancellationToken).ConfigureAwait(false);
    }

    private static object CreateSharedClient()
    {
        Type? clientType = Type.GetType(
            "Agency.GraphRAG.Code.TreeSitter.TreeSitterClient, Agency.GraphRAG.Code.TreeSitter",
            throwOnError: false);
        if (clientType is null)
        {
            throw new InvalidOperationException("Tree-sitter client is unavailable. Ensure the tree-sitter project is loaded.");
        }

        var constructor = clientType.GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            [typeof(string), typeof(string)],
            null)
            ?? throw new InvalidOperationException("TreeSitterClient constructor not found.");

        return constructor.Invoke(["node", null])
            ?? throw new InvalidOperationException("Failed to create the tree-sitter client.");
    }

    private static async Task<object> ParseWithClientAsync(object client, ChunkerInput input, CancellationToken cancellationToken)
    {
        Type clientType = client.GetType();
        object taskObject = clientType
            .GetMethod("ParseAsync", [typeof(string), typeof(Language), typeof(string), typeof(CancellationToken)])
            ?.Invoke(client, [input.Path, input.Language, input.Source, cancellationToken])
            ?? throw new InvalidOperationException("Tree-sitter ParseAsync could not be invoked.");

        dynamic parsed = await (dynamic)taskObject;
        return parsed.Root;
    }

    internal sealed record AstNodeAdapter(string Kind, string? Text, ChunkSourceRange? Range, IReadOnlyList<AstNodeAdapter> Children, string? FieldName = null)
    {
        public static AstNodeAdapter Create(object node, string source)
        {
            ArgumentNullException.ThrowIfNull(node);
            ArgumentNullException.ThrowIfNull(source);
            int[] lineOffsets = BuildLineOffsets(source);
            return Create(node, source, lineOffsets);
        }

        private static AstNodeAdapter Create(object node, string source, int[] lineOffsets)
        {
            Type type = node.GetType();
            string kind = (string?)type.GetProperty("Kind")?.GetValue(node)
                ?? throw new InvalidOperationException("AST node is missing a Kind property.");
            object? rangeValue = type.GetProperty("Range")?.GetValue(node);
            ChunkSourceRange? range = rangeValue is null ? null : CreateRange(rangeValue);
            string? text = (string?)type.GetProperty("Text")?.GetValue(node) ?? ExtractText(source, range, lineOffsets);
            string? fieldName = (string?)type.GetProperty("FieldName")?.GetValue(node);
            IEnumerable<object> children = ((System.Collections.IEnumerable?)type.GetProperty("Children")?.GetValue(node))
                ?.Cast<object>()
                ?? throw new InvalidOperationException("AST node is missing a Children property.");

            return new AstNodeAdapter(kind, text, range, children.Select(child => Create(child, source, lineOffsets)).ToArray(), fieldName);
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

        private static string? ExtractText(string source, ChunkSourceRange? range, int[] lineOffsets)
        {
            if (range is null)
            {
                return null;
            }

            int start = GetOffset(lineOffsets, range.StartLine, range.StartColumn);
            int end = GetOffset(lineOffsets, range.EndLine, range.EndColumn);
            return start >= 0 && end >= start && end <= source.Length
                ? source[start..end]
                : null;
        }

        private static int GetOffset(int[] lineOffsets, int targetLine, int targetColumn)
        {
            if ((uint)targetLine >= (uint)lineOffsets.Length)
            {
                return -1;
            }

            return lineOffsets[targetLine] + targetColumn;
        }

        private static int[] BuildLineOffsets(string source)
        {
            List<int> offsets = [0];
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    offsets.Add(i + 1);
                }
            }

            return [.. offsets];
        }
    }
}
