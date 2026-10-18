namespace MagicRDR.Core;

/// <summary>
/// A file or directory in the archive tree (the legacy <c>TOCSuperEntry</c>). Nodes belong to the
/// <see cref="Rpf6Archive"/> that created them; modify the tree through the archive's methods.
/// </summary>
public sealed class RpfNode
{
    private readonly List<RpfNode> _children = new();

    internal RpfNode(Rpf6Archive archive, RpfEntry entry)
    {
        Archive = archive;
        Entry = entry;
    }

    public Rpf6Archive Archive { get; }

    public RpfEntry Entry { get; internal set; }

    public RpfNode? Parent { get; internal set; }

    public IReadOnlyList<RpfNode> Children => _children;

    internal List<RpfNode> ChildList => _children;

    public bool IsDirectory => Entry is RpfDirectoryEntry;

    public RpfDirectoryEntry? AsDirectory => Entry as RpfDirectoryEntry;

    public RpfFileEntry? AsFile => Entry as RpfFileEntry;

    public bool IsRoot => Parent == null && IsDirectory;

    public uint NameHash => Entry.NameOffset;

    /// <summary>Resolved name, or <c>0xXXXXXXXX</c> if the hash is not in the name dictionary.</summary>
    public string Name => IsDirectory && Entry.NameOffset == 0 ? "root" : Archive.Names.Resolve(Entry.NameOffset);

    /// <summary>
    /// New content (from <see cref="Rpf6Archive.ReplaceFile"/> / <see cref="Rpf6Archive.AddFile"/>) that will be written on save.
    /// Always the <i>uncompressed</i> bytes; compression happens while saving. Owned by the archive.
    /// </summary>
    internal Stream? PendingData { get; set; }

    /// <summary>True for nodes that were read from the opened archive (as opposed to newly added files).</summary>
    internal bool ExistedInArchive { get; set; }

    /// <summary>True when this file has replacement content that has not been saved yet.</summary>
    public bool IsModified => PendingData != null;

    public bool IsResource => AsFile?.Flags.IsResource ?? false;

    public bool IsCompressed => AsFile is { } f && f.Flags.IsCompressed && !f.Flags.IsResource;

    /// <summary>Bytes occupied inside the archive (compressed size for compressed files).</summary>
    public long StoredSize => PendingData?.Length ?? AsFile?.SizeInArchive ?? 0;

    /// <summary>Size after decompression (plain files) or the stored size (resources).</summary>
    public long UncompressedSize
    {
        get
        {
            if (PendingData != null)
            {
                return PendingData.Length;
            }

            if (AsFile is not { } file)
            {
                return 0;
            }

            return file.Flags.IsCompressed && !file.Flags.IsResource ? file.Flags.GetTotalSize() : file.SizeInArchive;
        }
    }

    public string GetPath(string separator = "/")
    {
        var parts = new List<string>();
        for (RpfNode? node = this; node != null; node = node.Parent)
        {
            parts.Add(node.Name);
        }

        parts.Reverse();
        return string.Join(separator, parts);
    }

    /// <summary>
    /// Path made only of hashes (<c>root/0x560A49F7/0x00FEB5CC</c>). Unlike <see cref="GetPath"/> it does not change when
    /// names are imported, so it is what the saved session uses to find an entry again.
    /// </summary>
    public string GetHashPath()
    {
        var parts = new List<string>();
        for (RpfNode? node = this; node != null; node = node.Parent)
        {
            parts.Add(node.Parent == null ? "root" : $"0x{node.NameHash:X8}");
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    public RpfNode? FindChild(uint nameHash)
    {
        foreach (RpfNode child in _children)
        {
            if (child.Entry.NameOffset == nameHash)
            {
                return child;
            }
        }

        return null;
    }

    public RpfNode? FindChild(string name) => FindChild(RpfHash.GetHash(name));

    public override string ToString() => $"{(IsDirectory ? "[dir]" : "[file]")} {Name}";
}
