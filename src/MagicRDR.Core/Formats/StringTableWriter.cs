using System.Buffers.Binary;
using System.Text;

namespace MagicRDR.Core.Formats;

/// <summary>
/// Writes a <see cref="StringTableData"/> back into the resource layout, a port of the original tool's
/// <c>StringTable.Write</c>: a fixed 0x230-byte header, an info block, the bucket table, one node per entry
/// (key, entry pointer, next-node pointer), one entry record per node (key, text pointer) and finally the
/// NUL-terminated texts. Unlike the original, texts are written as UTF-8 (the original truncated to ASCII) and
/// duplicate keys do not crash it.
/// </summary>
public static class StringTableWriter
{
    /// <summary>The original pads Switch tables to a multiple of this many bytes.</summary>
    public const int DefaultPageSize = 16384;

    private const uint Tag = 0x50000000;
    private const uint Mask = 0x0FFFFFFF;
    private const int InfoOffset = 0x230;

    public static byte[] Write(StringTableData table, bool littleEndian = true, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(table);

        uint buckets = table.BucketCount == 0 ? 101u : table.BucketCount;
        if (buckets > ushort.MaxValue)
        {
            throw new ArgumentException("The bucket count must fit in 16 bits.", nameof(table));
        }

        IReadOnlyList<StringTableEntry> entries = table.Entries;
        var perBucket = new List<int>[buckets];
        for (int i = 0; i < entries.Count; i++)
        {
            (perBucket[entries[i].Key % buckets] ??= new List<int>()).Add(i);
        }

        var b = new GrowBuffer(littleEndian);
        b.U32(table.Vmt);
        b.U32(Tag | 0x20);
        b.U32(Tag | InfoOffset);
        b.U32(0);
        b.U32(0);
        for (int i = 0; i < 3; i++)
        {
            b.U32(0xCDCDCDCD);
        }

        b.U32(0);
        for (int i = 0; i < 131; i++)
        {
            b.U32(0xCDCDCDCD);
        }

        if (b.Length != InfoOffset)
        {
            throw new InvalidOperationException("Internal error: the string-table header has the wrong size.");
        }

        b.U32(buckets);
        int tablePointerAt = b.Length;
        b.U32(0);
        b.U16((ushort)buckets);
        b.U16((ushort)buckets);
        b.U32((uint)entries.Count);

        int bucketTableAt = b.Length;
        b.PatchU32(tablePointerAt, Tag | ((uint)bucketTableAt & Mask));
        b.Fill(0, checked(4 * (int)buckets));

        var entryPointerAt = new int[entries.Count];   // node word that will point to the entry record
        for (int index = 0; index < buckets; index++)
        {
            List<int>? list = perBucket[index];
            if (list == null)
            {
                continue;
            }

            b.PatchU32(bucketTableAt + 4 * index, Tag | ((uint)b.Length & Mask));
            for (int n = 0; n < list.Count; n++)
            {
                b.U32(entries[list[n]].Key);
                entryPointerAt[list[n]] = b.Length;
                b.U32(0);
                b.U32(n + 1 < list.Count ? Tag | ((uint)(b.Length + 4) & Mask) : 0u);
            }
        }

        var textPointerAt = new int[entries.Count];    // entry-record word that will point to the text
        for (int index = 0; index < buckets; index++)
        {
            List<int>? list = perBucket[index];
            if (list == null)
            {
                continue;
            }

            foreach (int entryIndex in list)
            {
                b.PatchU32(entryPointerAt[entryIndex], Tag | ((uint)b.Length & Mask));
                b.U32(entries[entryIndex].Key);
                textPointerAt[entryIndex] = b.Length;
                b.U32(0);
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            b.PatchU32(textPointerAt[i], Tag | ((uint)b.Length & Mask));
            b.Bytes(Encoding.UTF8.GetBytes(entries[i].Value ?? string.Empty));
            b.Bytes(new byte[] { 0 });
        }

        if (pageSize > 0)
        {
            int padded = Rsc05.RoundUpRepresentable(b.Length, pageSize);
            b.Fill(0xCD, padded - b.Length);
        }

        return b.ToArray();
    }

    /// <summary>Writes the table and packs it as a Switch resource (type 1), ready to replace the entry in the archive.</summary>
    public static async Task<byte[]> PackAsync(StringTableData table, RpfPlatform platform, CancellationToken cancellationToken = default)
    {
        byte[] raw = Write(table, littleEndian: platform == RpfPlatform.Switch, DefaultPageSize);
        return await RpfResourcePacker.PackAsync(raw, resourceType: 1, platform, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A tiny growable byte buffer with in-place patching, used by the table writers.</summary>
internal sealed class GrowBuffer
{
    private readonly bool _little;
    private byte[] _data = new byte[4096];

    public GrowBuffer(bool littleEndian) => _little = littleEndian;

    public int Length { get; private set; }

    private void Reserve(int extra)
    {
        if (Length + extra > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, Length + extra));
        }
    }

    public void U32(uint value)
    {
        Reserve(4);
        Store(Length, value);
        Length += 4;
    }

    public void U16(ushort value)
    {
        Reserve(2);
        Span<byte> span = _data.AsSpan(Length, 2);
        if (_little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, value);
        }

        Length += 2;
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        Reserve(bytes.Length);
        bytes.CopyTo(_data.AsSpan(Length));
        Length += bytes.Length;
    }

    public void Fill(byte value, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Reserve(count);
        _data.AsSpan(Length, count).Fill(value);
        Length += count;
    }

    public void PatchU32(int position, uint value) => Store(position, value);

    private void Store(int position, uint value)
    {
        Span<byte> span = _data.AsSpan(position, 4);
        if (_little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }
    }

    public byte[] ToArray() => _data.AsSpan(0, Length).ToArray();
}
