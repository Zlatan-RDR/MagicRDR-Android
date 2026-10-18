using System.Buffers.Binary;
using MagicRDR.Core;

namespace MagicRDR.Core.Tests;

/// <summary>Builds a tiny, unencrypted RPF6 (root + flat files) so the tests need no game data.</summary>
internal static class SyntheticRpf
{
    public sealed record Item(string Name, byte[] Content, bool Compressed);

    public static byte[] Build(params Item[] items)
    {
        // children must be sorted by name hash, like in real archives
        var sorted = items.OrderBy(i => RpfHash.GetHash(i.Name)).ToList();
        int entryCount = 1 + sorted.Count;
        int tocSize = (entryCount * 20 + 15) & ~15;

        using var ms = new MemoryStream();
        var header = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), Rpf6Archive.Magic);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), entryCount);
        // debug offset 0 + encryption flag 0 => unencrypted, detected as Xbox (deflate)
        ms.Write(header);

        var toc = new byte[tocSize];
        long dataPos = 0x1000;
        var blobs = new List<(long Offset, byte[] Data)>();

        // root directory: first child at index 1
        BinaryPrimitives.WriteUInt32BigEndian(toc.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(toc.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(toc.AsSpan(8), 0x80000000u | 1u);
        BinaryPrimitives.WriteInt32BigEndian(toc.AsSpan(12), sorted.Count);
        BinaryPrimitives.WriteInt32BigEndian(toc.AsSpan(16), 0);

        for (int i = 0; i < sorted.Count; i++)
        {
            Item item = sorted[i];
            byte[] stored = item.Compressed ? RpfCompression.Deflate(item.Content) : item.Content;
            int flag1 = item.Compressed ? (1 << 30) | item.Content.Length : item.Content.Length;

            Span<byte> e = toc.AsSpan((i + 1) * 20, 20);
            BinaryPrimitives.WriteUInt32BigEndian(e.Slice(0), RpfHash.GetHash(item.Name));
            BinaryPrimitives.WriteInt32BigEndian(e.Slice(4), stored.Length);
            BinaryPrimitives.WriteUInt32BigEndian(e.Slice(8), (uint)(dataPos / 8));
            BinaryPrimitives.WriteInt32BigEndian(e.Slice(12), flag1);
            BinaryPrimitives.WriteInt32BigEndian(e.Slice(16), 0);

            blobs.Add((dataPos, stored));
            dataPos = (dataPos + stored.Length + 7) / 8 * 8 + 8;
        }

        ms.Write(toc);
        foreach ((long offset, byte[] data) in blobs)
        {
            ms.SetLength(Math.Max(ms.Length, offset));
            ms.Position = offset;
            ms.Write(data);
        }

        return ms.ToArray();
    }
}
