using System.Buffers.Binary;
using System.Text;

namespace MagicRDR.Core.Formats;

/// <summary>One string of a string table: the hash of its key, its position in the hash bucket chain, and the text.</summary>
public sealed record StringTableEntry(uint Key, int Level, string Value);

/// <summary>A hash-keyed string table (resource type 1, the "<c>.wst</c>" files): VMT value, bucket count and the entries.</summary>
public sealed class StringTableData
{
    public uint Vmt { get; set; }

    /// <summary>Number of hash buckets (the original tool uses 101 for tables it creates).</summary>
    public uint BucketCount { get; set; } = 101;

    public List<StringTableEntry> Entries { get; } = new();
}

/// <summary>
/// Reads the unpacked data of a string-table resource (resource type 1). This is a bounds-checked port of the original
/// tool's <c>StringTable.Read</c>: a hash table whose buckets hold chains of (key, text pointer) nodes. Pointers carry a
/// <c>0x5...</c> tag in their top bits that is masked off. The matching writer is <see cref="StringTableWriter"/>.
/// </summary>
public static class StringTableReader
{
    private const uint PointerMask = 0x0FFFFFFF;
    private const uint MaxBuckets = 1_000_000;

    public static IReadOnlyList<StringTableEntry> Read(byte[] data, bool littleEndian) => ReadDocument(data, littleEndian).Entries;

    public static StringTableData ReadDocument(byte[] data, bool littleEndian)
    {
        ArgumentNullException.ThrowIfNull(data);

        uint Get(long position)
        {
            if (position < 0 || position + 4 > data.Length)
            {
                throw new InvalidDataException($"The string table points outside its data (offset {position}).");
            }

            ReadOnlySpan<byte> span = data.AsSpan((int)position, 4);
            return littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        string ReadCString(long position)
        {
            if (position < 0 || position >= data.Length)
            {
                throw new InvalidDataException($"A string points outside the table data (offset {position}).");
            }

            int end = Array.IndexOf(data, (byte)0, (int)position);
            if (end < 0)
            {
                end = data.Length;
            }

            return Encoding.UTF8.GetString(data, (int)position, end - (int)position);
        }

        var table = new StringTableData { Vmt = Get(0) };

        long info = Get(8) & PointerMask;
        uint bucketCount = Get(info);
        long bucketTable = Get(info + 4) & PointerMask;
        if (bucketCount == 0 || bucketCount > MaxBuckets)
        {
            throw new InvalidDataException($"The string table reports an unreasonable bucket count ({bucketCount}).");
        }

        table.BucketCount = bucketCount;
        var visited = new HashSet<long>();

        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            long head = Get(bucketTable + 4L * bucket) & PointerMask;
            if (head == 0)
            {
                continue;
            }

            long entry = Get(head + 4) & PointerMask;
            long next = Get(head + 8) & PointerMask;
            int level = 0;

            while (entry > 0)
            {
                if (!visited.Add(entry))
                {
                    throw new InvalidDataException("The string table contains a loop.");
                }

                uint key = Get(entry);
                long text = Get(entry + 4) & PointerMask;
                table.Entries.Add(new StringTableEntry(key, level, ReadCString(text)));

                if (next != 0 && key > 0)
                {
                    long node = next;
                    entry = Get(node + 4) & PointerMask;
                    next = Get(node + 8) & PointerMask;
                }
                else
                {
                    entry = 0;
                }

                level++;
            }
        }

        return table;
    }
}
