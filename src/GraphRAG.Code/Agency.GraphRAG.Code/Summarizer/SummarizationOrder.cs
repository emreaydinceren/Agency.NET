using Agency.GraphRAG.Code.Chunker;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Orders chunks for summarization so parents, interfaces, and base types are summarized before dependents.
/// </summary>
internal static class SummarizationOrder
{
    /// <summary>
    /// Orders chunks using parent, interface, and inheritance relationships.
    /// </summary>
    /// <param name="chunks">The chunks to order.</param>
    /// <param name="warningSink">An optional warning sink used when a cycle is detected.</param>
    /// <returns>The ordered chunks.</returns>
    public static IReadOnlyList<Chunk> Order(IReadOnlyList<Chunk> chunks, Action<string>? warningSink = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count <= 1)
        {
            return chunks;
        }

        ChunkNode[] nodes = chunks
            .Select((chunk, index) => new ChunkNode(chunk, index))
            .OrderBy(node => node, ChunkNodeFileOrderComparer.Instance)
            .ToArray();

        Dictionary<string, ChunkNode> nodesById = nodes.ToDictionary(node => node.Chunk.Id, StringComparer.Ordinal);
        Dictionary<string, List<ChunkNode>> nodesByQualifiedName = BuildNameMap(nodes, static chunk => chunk.FullyQualifiedName);
        Dictionary<string, List<ChunkNode>> nodesBySimpleName = BuildNameMap(nodes, static chunk => chunk.Name);

        foreach (ChunkNode node in nodes)
        {
            AddDependency(node.Chunk.ParentId, node, nodesById);

            foreach (string dependencyName in node.Chunk.Inherits ?? [])
            {
                if (TryResolveDependency(node, dependencyName, nodesByQualifiedName, nodesBySimpleName, out ChunkNode? dependency)
                    && dependency is not null)
                {
                    AddDependency(dependency, node);
                }
            }

            foreach (string dependencyName in node.Chunk.Implements ?? [])
            {
                if (TryResolveDependency(node, dependencyName, nodesByQualifiedName, nodesBySimpleName, out ChunkNode? dependency)
                    && dependency is not null)
                {
                    AddDependency(dependency, node);
                }
            }
        }

        List<ChunkNode> ready = nodes.Where(static node => node.InDegree == 0).ToList();
        ready.Sort(ChunkNodeFileOrderComparer.Instance);

        List<Chunk> ordered = new(nodes.Length);
        while (ready.Count > 0)
        {
            ChunkNode next = ready[0];
            ready.RemoveAt(0);
            ordered.Add(next.Chunk);

            foreach (ChunkNode dependent in next.Dependents.Order(ChunkNodeFileOrderComparer.Instance))
            {
                dependent.InDegree--;
                if (dependent.InDegree == 0)
                {
                    InsertInFileOrder(ready, dependent);
                }
            }
        }

        if (ordered.Count == nodes.Length)
        {
            return ordered;
        }

        ChunkNode[] cycleNodes = nodes.Where(static node => node.InDegree > 0).OrderBy(node => node, ChunkNodeFileOrderComparer.Instance).ToArray();
        warningSink?.Invoke($"Summarization order cycle detected for: {string.Join(", ", cycleNodes.Select(static node => node.Chunk.FullyQualifiedName))}. Falling back to file order for the cycle.");
        ordered.AddRange(cycleNodes.Select(static node => node.Chunk));
        return ordered;
    }

    private static Dictionary<string, List<ChunkNode>> BuildNameMap(IEnumerable<ChunkNode> nodes, Func<Chunk, string> keySelector)
    {
        Dictionary<string, List<ChunkNode>> map = new(StringComparer.Ordinal);
        foreach (ChunkNode node in nodes)
        {
            string key = keySelector(node.Chunk);
            if (!map.TryGetValue(key, out List<ChunkNode>? bucket))
            {
                bucket = [];
                map[key] = bucket;
            }

            bucket.Add(node);
        }

        return map;
    }

    private static bool TryResolveDependency(
        ChunkNode current,
        string dependencyName,
        IReadOnlyDictionary<string, List<ChunkNode>> nodesByQualifiedName,
        IReadOnlyDictionary<string, List<ChunkNode>> nodesBySimpleName,
        out ChunkNode? dependency)
    {
        if (nodesByQualifiedName.TryGetValue(dependencyName, out List<ChunkNode>? exactQualifiedMatch))
        {
            dependency = exactQualifiedMatch[0];
            return true;
        }

        string? currentNamespace = GetNamespace(current.Chunk.FullyQualifiedName);
        if (!string.IsNullOrWhiteSpace(currentNamespace)
            && nodesByQualifiedName.TryGetValue($"{currentNamespace}.{dependencyName}", out List<ChunkNode>? namespaceQualifiedMatch))
        {
            dependency = namespaceQualifiedMatch[0];
            return true;
        }

        if (!nodesBySimpleName.TryGetValue(dependencyName, out List<ChunkNode>? simpleMatches) || simpleMatches.Count == 0)
        {
            dependency = null;
            return false;
        }

        List<ChunkNode> samePathMatches = simpleMatches
            .Where(match => string.Equals(match.Chunk.Path, current.Chunk.Path, StringComparison.Ordinal))
            .OrderBy(match => match, ChunkNodeFileOrderComparer.Instance)
            .ToList();

        if (samePathMatches.Count > 0)
        {
            dependency = samePathMatches[0];
            return true;
        }

        dependency = simpleMatches.OrderBy(match => match, ChunkNodeFileOrderComparer.Instance).First();
        return true;
    }

    private static string? GetNamespace(string fullyQualifiedName)
    {
        int separatorIndex = fullyQualifiedName.LastIndexOf('.');
        return separatorIndex <= 0 ? null : fullyQualifiedName[..separatorIndex];
    }

    private static void AddDependency(string? dependencyId, ChunkNode node, IReadOnlyDictionary<string, ChunkNode> nodesById)
    {
        if (string.IsNullOrWhiteSpace(dependencyId) || !nodesById.TryGetValue(dependencyId, out ChunkNode? dependency))
        {
            return;
        }

        AddDependency(dependency, node);
    }

    private static void AddDependency(ChunkNode dependency, ChunkNode node)
    {
        if (ReferenceEquals(dependency, node) || !dependency.Dependents.Add(node))
        {
            return;
        }

        node.InDegree++;
    }

    private static void InsertInFileOrder(List<ChunkNode> ready, ChunkNode node)
    {
        int index = ready.BinarySearch(node, ChunkNodeFileOrderComparer.Instance);
        ready.Insert(index < 0 ? ~index : index, node);
    }

    private sealed class ChunkNode(Chunk chunk, int originalIndex)
    {
        public Chunk Chunk { get; } = chunk;

        public int OriginalIndex { get; } = originalIndex;

        public int InDegree { get; set; }

        public HashSet<ChunkNode> Dependents { get; } = [];
    }

    private sealed class ChunkNodeFileOrderComparer : IComparer<ChunkNode>
    {
        public static ChunkNodeFileOrderComparer Instance { get; } = new();

        public int Compare(ChunkNode? x, ChunkNode? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int comparison = string.Compare(x.Chunk.Path, y.Chunk.Path, StringComparison.Ordinal);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.Chunk.Range.StartLine.CompareTo(y.Chunk.Range.StartLine);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.Chunk.Range.StartColumn.CompareTo(y.Chunk.Range.StartColumn);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.Chunk.Range.EndLine.CompareTo(y.Chunk.Range.EndLine);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = x.Chunk.Range.EndColumn.CompareTo(y.Chunk.Range.EndColumn);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.Compare(x.Chunk.FullyQualifiedName, y.Chunk.FullyQualifiedName, StringComparison.Ordinal);
            if (comparison != 0)
            {
                return comparison;
            }

            return x.OriginalIndex.CompareTo(y.OriginalIndex);
        }
    }
}
