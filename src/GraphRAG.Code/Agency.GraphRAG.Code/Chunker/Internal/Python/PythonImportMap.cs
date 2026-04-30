namespace Agency.GraphRAG.Code.Chunker.Internal.Python;

/// <summary>
/// Tracks Python imports and simple alias resolution for chunking.
/// </summary>
internal sealed class PythonImportMap
{
    private readonly Dictionary<string, string> _aliases;

    private PythonImportMap(IReadOnlyList<ImportReference> imports, Dictionary<string, string> aliases)
    {
        Imports = imports;
        _aliases = aliases;
    }

    /// <summary>
    /// Gets the import references visible to the file.
    /// </summary>
    public IReadOnlyList<ImportReference> Imports { get; }

    /// <summary>
    /// Builds an import map from the module root.
    /// </summary>
    /// <param name="root">The module root.</param>
    /// <returns>The import map.</returns>
    public static PythonImportMap Create(PythonChunker.AstNodeAdapter root)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<ImportReference> imports = [];
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);

        foreach (PythonChunker.AstNodeAdapter child in root.Children)
        {
            if (child.Kind == "import_statement")
            {
                AddImportStatement(child, imports, aliases);
            }
            else if (child.Kind == "import_from_statement")
            {
                AddImportFromStatement(child, imports, aliases);
            }
        }

        return new PythonImportMap(imports, aliases);
    }

    /// <summary>
    /// Resolves an imported alias to a qualified name when available.
    /// </summary>
    /// <param name="typeName">The source type name.</param>
    /// <returns>The resolved qualified name, or the original text.</returns>
    public string ResolveQualifiedName(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        string trimmed = typeName.Trim();
        int dotIndex = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex >= 0)
        {
            string head = trimmed[..dotIndex];
            if (_aliases.TryGetValue(head, out string? resolvedHead))
            {
                return resolvedHead + trimmed[dotIndex..];
            }

            return trimmed;
        }

        return _aliases.TryGetValue(trimmed, out string? resolved) ? resolved : trimmed;
    }

    private static void AddImportStatement(
        PythonChunker.AstNodeAdapter node,
        ICollection<ImportReference> imports,
        IDictionary<string, string> aliases)
    {
        foreach (PythonChunker.AstNodeAdapter importNode in node.Children.Where(static child => child.FieldName == "name"))
        {
            if (importNode.Kind == "aliased_import")
            {
                PythonChunker.AstNodeAdapter? moduleNode = importNode.Children.FirstOrDefault(static child => child.FieldName == "name");
                PythonChunker.AstNodeAdapter? aliasNode = importNode.Children.FirstOrDefault(static child => child.FieldName == "alias");
                if (string.IsNullOrWhiteSpace(moduleNode?.Text))
                {
                    continue;
                }

                string moduleSource = moduleNode.Text!;
                string? alias = aliasNode?.Text;
                imports.Add(new ImportReference(moduleSource, [], IsRelative: moduleSource.StartsWith(".", StringComparison.Ordinal), alias));
                aliases[alias ?? GetLastSegment(moduleSource)] = moduleSource;
                continue;
            }

            if (string.IsNullOrWhiteSpace(importNode.Text))
            {
                continue;
            }

            string source = importNode.Text!;
            imports.Add(new ImportReference(source, [], IsRelative: source.StartsWith(".", StringComparison.Ordinal)));
            aliases[GetLastSegment(source)] = source;
        }
    }

    private static void AddImportFromStatement(
        PythonChunker.AstNodeAdapter node,
        ICollection<ImportReference> imports,
        IDictionary<string, string> aliases)
    {
        PythonChunker.AstNodeAdapter? moduleNode = node.Children.FirstOrDefault(static child => child.FieldName == "module_name");
        if (string.IsNullOrWhiteSpace(moduleNode?.Text))
        {
            return;
        }

        string source = moduleNode.Text!;
        bool isRelative = source.StartsWith(".", StringComparison.Ordinal);
        foreach (PythonChunker.AstNodeAdapter importNode in node.Children.Where(static child => child.FieldName == "name"))
        {
            if (importNode.Kind == "aliased_import")
            {
                PythonChunker.AstNodeAdapter? nameNode = importNode.Children.FirstOrDefault(static child => child.FieldName == "name");
                PythonChunker.AstNodeAdapter? aliasNode = importNode.Children.FirstOrDefault(static child => child.FieldName == "alias");
                if (string.IsNullOrWhiteSpace(nameNode?.Text))
                {
                    continue;
                }

                string importedName = nameNode.Text!;
                string? alias = aliasNode?.Text;
                imports.Add(new ImportReference(source, [importedName], isRelative, alias));
                aliases[alias ?? GetLastSegment(importedName)] = $"{source}.{importedName}";
                continue;
            }

            if (string.IsNullOrWhiteSpace(importNode.Text))
            {
                continue;
            }

            string importedSymbol = importNode.Text!;
            imports.Add(new ImportReference(source, [importedSymbol], isRelative));
            aliases[GetLastSegment(importedSymbol)] = $"{source}.{importedSymbol}";
        }
    }

    private static string GetLastSegment(string value)
    {
        int dotIndex = value.LastIndexOf('.');
        return dotIndex >= 0 ? value[(dotIndex + 1)..] : value;
    }
}
