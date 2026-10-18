using System.Buffers.Binary;

namespace MagicRDR.Core;

/// <summary>
/// Header of a packed RAGE resource (<c>RSC05/06/85/86</c>). Replacing a resource entry needs the type and flag
/// words from the replacement file's own header, exactly like the original import dialog did.
/// </summary>
public sealed class RpfResourceInfo
{
    public const uint Rsc05Magic = 88298322u;
    public const uint Rsc06Magic = 105075538u;
    public const uint Rsc85Magic = 2235781970u;
    public const uint Rsc86Magic = 2252559186u;

    public bool IsResource { get; init; }

    public string Version { get; init; } = "None";

    public int ResourceType { get; init; }

    public int Flag1 { get; init; }

    public int Flag2 { get; init; }

    public static RpfResourceInfo NotAResource { get; } = new();

    /// <summary>
    /// Inspects the first bytes of <paramref name="stream"/> (from position 0) and rewinds it to 0.
    /// Switch archives store resource headers little-endian, the other platforms big-endian.
    /// </summary>
    public static RpfResourceInfo Read(Stream stream, RpfPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            stream.Position = 0;
            Span<byte> head = stackalloc byte[16];
            int read = ReadUpTo(stream, head);
            if (read < 12)
            {
                return NotAResource;
            }

            bool little = platform == RpfPlatform.Switch;
            uint ident = little
                ? BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(0, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(head.Slice(0, 4));
            int type = little
                ? BinaryPrimitives.ReadInt32LittleEndian(head.Slice(4, 4))
                : BinaryPrimitives.ReadInt32BigEndian(head.Slice(4, 4));
            int flag1 = little
                ? BinaryPrimitives.ReadInt32LittleEndian(head.Slice(8, 4))
                : BinaryPrimitives.ReadInt32BigEndian(head.Slice(8, 4));

            switch (ident)
            {
                case Rsc05Magic:
                case Rsc06Magic:
                    return new RpfResourceInfo
                    {
                        IsResource = true,
                        Version = ident == Rsc05Magic ? "RSC05" : "RSC06",
                        ResourceType = type,
                        Flag1 = flag1,
                        Flag2 = 0,
                    };

                case Rsc85Magic:
                case Rsc86Magic:
                    if (read < 16)
                    {
                        return NotAResource;
                    }

                    int flag2 = little
                        ? BinaryPrimitives.ReadInt32LittleEndian(head.Slice(12, 4))
                        : BinaryPrimitives.ReadInt32BigEndian(head.Slice(12, 4));
                    return new RpfResourceInfo
                    {
                        IsResource = true,
                        Version = ident == Rsc85Magic ? "RSC85" : "RSC86",
                        ResourceType = type,
                        Flag1 = flag1,
                        Flag2 = flag2,
                    };

                default:
                    return NotAResource;
            }
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static int ReadUpTo(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(total));
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
