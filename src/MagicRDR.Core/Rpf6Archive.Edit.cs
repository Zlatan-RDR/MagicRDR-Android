namespace MagicRDR.Core;

public sealed partial class Rpf6Archive
{
    private const long MaxPlainFileSize = 0x3FFFFFFF; // the uncompressed size lives in the low 30 bits of Flag1

    /// <summary>
    /// Replaces the content of an existing file (the "translation injection" operation).
    /// <para>
    /// <paramref name="content"/> must be readable and seekable and is <b>owned by the archive from now on</b>
    /// (disposed when replaced again, removed, or when the archive is disposed). If this method throws, the caller still owns it.
    /// </para>
    /// <list type="bullet">
    /// <item><b>Plain files</b>: pass the raw (uncompressed) bytes; they are deflated (Zstandard on Switch) while saving if the
    /// old entry was compressed or the name looks like text (<see cref="RpfCompressionMode.Auto"/>).</item>
    /// <item><b>Resources</b> (RSC05/06/85/86): pass the complete, already packed resource file. Its header supplies the
    /// resource type and size flags and the bytes are stored unchanged, like the original tool did.</item>
    /// </list>
    /// Nothing is written until <see cref="SaveAsync"/>.
    /// </summary>
    public RpfNode ReplaceFile(RpfNode node, Stream content, RpfImportOptions? options = null)
    {
        ThrowIfDisposed();
        EnsureOwned(node);
        ArgumentNullException.ThrowIfNull(content);
        options ??= new RpfImportOptions();

        if (node.AsFile is not { } oldFile)
        {
            throw new ArgumentException("Only files can be replaced.", nameof(node));
        }

        ValidateContentStream(content);

        RpfResourceInfo info = RpfResourceInfo.Read(content, Platform);
        if (options.StrictResourceCheck && info.IsResource != oldFile.Flags.IsResource)
        {
            throw new InvalidDataException(oldFile.Flags.IsResource
                ? $"'{node.Name}' is a packed resource, but the replacement file is not (it has no RSC header). Provide a packed resource file."
                : $"'{node.Name}' is a plain file, but the replacement has a resource header. Provide the raw file contents instead.");
        }

        string effectiveName = string.IsNullOrWhiteSpace(options.NewName) ? node.Name : options.NewName.Trim();
        uint nameHash = string.IsNullOrWhiteSpace(options.NewName) ? oldFile.NameOffset : RpfHash.GetHash(effectiveName);

        if (nameHash != oldFile.NameOffset && node.Parent?.FindChild(nameHash) is { } clash && !ReferenceEquals(clash, node))
        {
            throw new InvalidOperationException($"A file named '{effectiveName}' already exists in this folder.");
        }

        bool wasCompressedPlain = oldFile.Flags.IsCompressed && !oldFile.Flags.IsResource;
        RpfFileEntry entry = BuildFileEntry(nameHash, effectiveName, content.Length, info, options.Compression, wasCompressedPlain);

        // Keep the file's physical position in the rebuilt archive (a replaced entry is not moved to the end).
        entry.KeepOffset = oldFile.KeepOffset ?? (node.ExistedInArchive ? (long?)oldFile.GetOffset() : null);

        if (!string.IsNullOrWhiteSpace(options.NewName))
        {
            Names.Add(effectiveName);
        }

        node.PendingData?.Dispose();
        node.PendingData = content;
        node.Entry = entry;
        HasPendingChanges = true;
        return node;
    }

    /// <summary>Adds a new file to <paramref name="directory"/>. Ownership of <paramref name="content"/> rules are the same as <see cref="ReplaceFile"/>.</summary>
    public RpfNode AddFile(RpfNode directory, string name, Stream content, RpfImportOptions? options = null)
    {
        ThrowIfDisposed();
        EnsureOwned(directory);
        ArgumentNullException.ThrowIfNull(content);
        ValidateNewName(directory, name);
        ValidateContentStream(content);
        options ??= new RpfImportOptions();

        RpfResourceInfo info = RpfResourceInfo.Read(content, Platform);
        uint hash = RpfHash.GetHash(name);
        RpfFileEntry entry = BuildFileEntry(hash, name, content.Length, info, options.Compression, wasCompressed: false);

        Names.Add(name);
        var node = new RpfNode(this, entry)
        {
            Parent = directory,
            PendingData = content,
            ExistedInArchive = false,
        };
        directory.ChildList.Add(node);
        HasPendingChanges = true;
        return node;
    }

    public RpfNode AddDirectory(RpfNode parent, string name)
    {
        ThrowIfDisposed();
        EnsureOwned(parent);
        ValidateNewName(parent, name);

        Names.Add(name);
        var node = new RpfNode(this, new RpfDirectoryEntry { NameOffset = RpfHash.GetHash(name) })
        {
            Parent = parent,
            ExistedInArchive = false,
        };
        parent.ChildList.Add(node);
        HasPendingChanges = true;
        return node;
    }

    /// <summary>Removes a file or folder (and everything below it) from the tree. The root cannot be removed.</summary>
    public void RemoveNode(RpfNode node)
    {
        ThrowIfDisposed();
        EnsureOwned(node);

        if (node.Parent == null)
        {
            throw new InvalidOperationException("The root directory cannot be removed.");
        }

        foreach (RpfNode descendant in EnumerateDescendants(node))
        {
            descendant.PendingData?.Dispose();
            descendant.PendingData = null;
        }

        node.Parent.ChildList.Remove(node);
        node.Parent = null;
        HasPendingChanges = true;
    }

    private static void ValidateNewName(RpfNode directory, string name)
    {
        if (!directory.IsDirectory)
        {
            throw new ArgumentException("Items can only be added to a directory.", nameof(directory));
        }

        if (string.IsNullOrWhiteSpace(name) || name.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            throw new ArgumentException("The name must not be empty or contain path separators.", nameof(name));
        }

        if (directory.FindChild(name) != null)
        {
            throw new InvalidOperationException($"'{name}' already exists in this folder.");
        }
    }

    private static void ValidateContentStream(Stream content)
    {
        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException("The replacement stream must be readable and seekable.", nameof(content));
        }

        if (content.Length > MaxPlainFileSize)
        {
            throw new InvalidDataException("The file is too large for an RPF6 entry (limit is 1 GiB).");
        }
    }

    private static RpfFileEntry BuildFileEntry(
        uint nameHash,
        string name,
        long length,
        RpfResourceInfo info,
        RpfCompressionMode mode,
        bool wasCompressed)
    {
        var entry = new RpfFileEntry
        {
            NameOffset = nameHash,
            SizeInArchive = (int)length,
        };

        if (info.IsResource)
        {
            entry.Flags = new RpfFlags(info.Flag1, info.Flag2);
            if (!entry.Flags.IsResource)
            {
                throw new InvalidDataException("The resource header's flags do not have the resource bit set; the file looks damaged.");
            }

            entry.ResourceType = (byte)info.ResourceType;
            return entry;
        }

        bool compress = mode switch
        {
            RpfCompressionMode.Always => true,
            RpfCompressionMode.Never => false,
            _ => wasCompressed || RpfFileTypes.IsTextFile(name),
        };

        entry.Flags = new RpfFlags();
        entry.Flags.IsCompressed = compress;
        entry.Flags.SetUncompressedSize((int)length);
        return entry;
    }
}
