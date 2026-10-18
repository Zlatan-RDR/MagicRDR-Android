namespace MagicRDR.Core;

/// <summary>Console family the archive came from. Only <see cref="Switch"/> changes how plain entries are compressed (Zstandard instead of deflate).</summary>
public enum RpfPlatform
{
    Unknown = 0,
    PS3 = 4096,
    Xbox = 8192,
    Switch = 16384,
}

/// <summary>
/// The two 32-bit flag words of a file entry. Ported from <c>ResourceUtils.FlagInfo</c>, reduced to the
/// parts the archive layer needs (resource bit, compression bit, total sizes).
/// <list type="bullet">
/// <item>Plain file: <c>Flag1 = (compressed &lt;&lt; 30) | uncompressedSize</c>.</item>
/// <item>Resource:  <c>Flag1</c> bit 31 set; page sizes are packed in <c>Flag1</c> (RSC05) or <c>Flag2</c> (RSC85).</item>
/// </list>
/// </summary>
public sealed class RpfFlags
{
    public RpfFlags()
    {
    }

    public RpfFlags(int flag1, int flag2)
    {
        Flag1 = flag1;
        Flag2 = flag2;
    }

    public int Flag1 { get; set; }

    public int Flag2 { get; set; }

    public bool IsResource => ((uint)Flag1 & 0x80000000u) != 0;

    /// <summary>RSC85/86 style resource (extended size bits live in <see cref="Flag2"/>).</summary>
    public bool IsExtendedFlags => ((uint)Flag2 & 0x80000000u) != 0;

    /// <summary>Whether the stored bytes are compressed (plain files only; resources carry their own compression).</summary>
    public bool IsCompressed
    {
        get => !IsExtendedFlags && ((Flag1 >> 30) & 1) == 1;
        set
        {
            if (IsExtendedFlags)
            {
                return;
            }

            Flag1 = (Flag1 & unchecked((int)0xBFFFFFFF)) | ((value ? 1 : 0) << 30);
        }
    }

    /// <summary>Uncompressed size for plain files; virtual + physical size for resources.</summary>
    public int GetTotalSize()
    {
        if (!IsResource)
        {
            return Flag1 & unchecked((int)0xBFFFFFFF);
        }

        return IsExtendedFlags
            ? Rsc85TotalVirtualSize + Rsc85TotalPhysicalSize
            : Rsc05TotalVirtualSize + Rsc05TotalPhysicalSize;
    }

    /// <summary>Sets the uncompressed size of a plain (non-resource) file, preserving the compression bit.</summary>
    public void SetUncompressedSize(int size)
    {
        if (IsResource)
        {
            throw new InvalidOperationException("Resource sizes are encoded in their page flags and cannot be set directly.");
        }

        Flag1 = unchecked((int)(((long)Flag1 & 0x40000000L) | ((long)size & 0xBFFFFFFFL)));
    }

    public RpfFlags Clone() => new(Flag1, Flag2);

    private int Rsc05TotalVirtualSize => (Flag1 & 2047) << (((Flag1 >> 11) & 15) + 8);

    private int Rsc05TotalPhysicalSize => ((Flag1 >> 15) & 2047) << (((Flag1 >> 26) & 15) + 8);

    private int Rsc85TotalVirtualSize => (Flag2 & 16383) << 12;

    private int Rsc85TotalPhysicalSize => ((Flag2 >> 14) & 16383) << 12;
}
