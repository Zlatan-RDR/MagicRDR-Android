namespace MagicRDR.Core;

/// <summary>A raw 20-byte table-of-contents record.</summary>
public abstract class RpfEntry
{
    /// <summary>Jenkins hash of the (lower-cased) name. RPF6 does not store names.</summary>
    public uint NameOffset { get; set; }
}

public sealed class RpfDirectoryEntry : RpfEntry
{
    public int Flags { get; set; }

    /// <summary>Index of the first child in the TOC (children are contiguous and sorted by name hash).</summary>
    public int ContentEntryIndex { get; set; }

    public int ContentEntryCount { get; set; }

    public int Unknown { get; set; }
}

public sealed class RpfFileEntry : RpfEntry
{
    public RpfFlags Flags { get; set; } = new();

    /// <summary>Number of bytes the entry occupies in the archive (compressed size for compressed files).</summary>
    public int SizeInArchive { get; set; }

    /// <summary>Offset exactly as stored in the TOC (offset / 8; for resources the low byte is the resource type).</summary>
    public uint RawOffset { get; set; }

    /// <summary>
    /// For replaced entries: the original archive offset, used only to keep the rebuilt archive's file order stable.
    /// </summary>
    public long? KeepOffset { get; set; }

    /// <summary>Resource type (low byte of <see cref="RawOffset"/>); 0 for plain files.</summary>
    public byte ResourceType
    {
        get => (byte)(RawOffset & 0xFF);
        set => RawOffset = (RawOffset & 0xFFFFFF00u) | value;
    }

    /// <summary>Absolute byte offset of the entry's data inside the archive.</summary>
    public long GetOffset() => Flags.IsResource
        ? (long)(RawOffset & 0x7FFFFF00u) * 8L
        : (long)(RawOffset & 0x7FFFFFFFu) * 8L;

    internal static uint EncodeOffset(long offset, bool isResource, byte resourceType)
    {
        if ((offset & 7) != 0)
        {
            throw new InvalidOperationException($"Entry offset {offset} is not 8-byte aligned.");
        }

        long units = offset / 8;
        if (isResource)
        {
            if ((units & 0xFF) != 0)
            {
                throw new InvalidOperationException($"Resource offset {offset} is not 2048-byte aligned.");
            }

            if (units > 0x7FFFFF00L)
            {
                throw new InvalidOperationException("The archive is too large to address this resource.");
            }

            return (uint)units | resourceType;
        }

        if (units > 0x7FFFFFFFL)
        {
            throw new InvalidOperationException("The archive is too large to address this file.");
        }

        return (uint)units;
    }
}
