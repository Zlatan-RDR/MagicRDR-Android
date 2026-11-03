namespace MagicRDR.Core;

/// <summary>How the entries of one folder are ordered in the file list. Folders always come first, whatever the mode.</summary>
public enum NodeSortMode
{
    NameAscending,
    NameDescending,
    SizeAscending,
    SizeDescending,
    TypeAscending,
    TypeDescending,

    /// <summary>The order of the entries in the archive's table of contents (RPF6 stores no modification dates).</summary>
    OriginalOrder,
    OriginalOrderReversed,
}

/// <summary>The few facts about one entry that sorting needs. Kept separate from <see cref="RpfNode"/> so the sorting rules are easy to test.</summary>
/// <param name="IsDirectory">True for folders.</param>
/// <param name="Name">The resolved name, or the <c>0x…</c> hash text when the name is unknown.</param>
/// <param name="IsNamed">True when the name was found in the names dictionary.</param>
/// <param name="Size">Size after decompression (0 for folders).</param>
/// <param name="Index">Position of the entry among its siblings in the archive's table of contents.</param>
public readonly record struct NodeSortKey(bool IsDirectory, string Name, bool IsNamed, long Size, int Index)
{
    /// <summary>Lower-case extension with its dot (".csc"), or an empty string when the name has none (every unresolved <c>0x…</c> name).</summary>
    public string Extension
    {
        get
        {
            int dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? string.Empty : Name.Substring(dot).ToLowerInvariant();
        }
    }
}

/// <summary>Orders the entries of a folder. Pure logic: no UI, no archive access.</summary>
public static class NodeSorter
{
    /// <summary>Builds the sort key of a node. <paramref name="index"/> is its position among its siblings.</summary>
    public static NodeSortKey KeyOf(RpfNode node, int index, RpfNameDictionary names)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(names);

        return new NodeSortKey(
            node.IsDirectory,
            node.Name,
            names.Contains(node.NameHash),
            node.IsDirectory ? 0 : node.UncompressedSize,
            index);
    }

    /// <summary>
    /// Returns the items in the requested order. Folders are always first. The sort is deterministic:
    /// entries that compare equal keep their table-of-contents order.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, NodeSortKey> keyOf, NodeSortMode mode)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keyOf);

        var keyed = new List<KeyValuePair<T, NodeSortKey>>();
        foreach (T item in items)
        {
            keyed.Add(new KeyValuePair<T, NodeSortKey>(item, keyOf(item)));
        }

        IComparer<NodeSortKey> comparer = Comparer<NodeSortKey>.Create((a, b) => Compare(a, b, mode));
        return keyed.OrderBy(pair => pair.Value, comparer).Select(pair => pair.Key).ToList();
    }

    private static int Compare(NodeSortKey a, NodeSortKey b, NodeSortMode mode)
    {
        if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;   // folders first, in every mode
        }

        int result = a.IsDirectory ? CompareFolders(a, b, mode) : CompareFiles(a, b, mode);
        return result != 0 ? result : a.Index.CompareTo(b.Index);
    }

    private static int CompareFolders(NodeSortKey a, NodeSortKey b, NodeSortMode mode) => mode switch
    {
        NodeSortMode.NameDescending => CompareNames(a, b, descending: true),
        NodeSortMode.OriginalOrder => a.Index.CompareTo(b.Index),
        NodeSortMode.OriginalOrderReversed => b.Index.CompareTo(a.Index),
        _ => CompareNames(a, b, descending: false),   // size and type mean nothing for a folder
    };

    private static int CompareFiles(NodeSortKey a, NodeSortKey b, NodeSortMode mode) => mode switch
    {
        NodeSortMode.NameDescending => CompareNames(a, b, descending: true),
        NodeSortMode.SizeAscending => ThenByName(a.Size.CompareTo(b.Size), a, b),
        NodeSortMode.SizeDescending => ThenByName(b.Size.CompareTo(a.Size), a, b),
        NodeSortMode.TypeAscending => ThenByName(CompareTypes(a, b, descending: false), a, b),
        NodeSortMode.TypeDescending => ThenByName(CompareTypes(a, b, descending: true), a, b),
        NodeSortMode.OriginalOrder => a.Index.CompareTo(b.Index),
        NodeSortMode.OriginalOrderReversed => b.Index.CompareTo(a.Index),
        _ => CompareNames(a, b, descending: false),
    };

    private static int ThenByName(int first, NodeSortKey a, NodeSortKey b) =>
        first != 0 ? first : CompareNames(a, b, descending: false);

    /// <summary>Known names first (in either direction), unresolved <c>0x…</c> names last, each group ordered by name.</summary>
    private static int CompareNames(NodeSortKey a, NodeSortKey b, bool descending)
    {
        if (a.IsNamed != b.IsNamed)
        {
            return a.IsNamed ? -1 : 1;
        }

        int result = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return descending ? -result : result;
    }

    /// <summary>Orders by extension; entries without an extension always go to the bottom.</summary>
    private static int CompareTypes(NodeSortKey a, NodeSortKey b, bool descending)
    {
        string x = a.Extension;
        string y = b.Extension;

        if (x.Length == 0 || y.Length == 0)
        {
            if (x.Length == y.Length)
            {
                return 0;
            }

            return x.Length == 0 ? 1 : -1;
        }

        int result = string.CompareOrdinal(x, y);
        return descending ? -result : result;
    }
}
