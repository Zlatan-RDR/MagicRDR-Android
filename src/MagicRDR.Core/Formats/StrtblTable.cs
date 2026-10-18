using System.Buffers.Binary;
using System.Text;

namespace MagicRDR.Core.Formats;

/// <summary>One language of a multi-language table: its texts and the 20-byte record the game keeps after each one.</summary>
public sealed class StrtblSegment
{
    public string Language { get; init; } = string.Empty;

    public uint UnknownValue { get; set; }

    public List<string> Strings { get; } = new();

    /// <summary>The 20 bytes that followed each text in the file (kept so unedited data is written back unchanged).</summary>
    public List<byte[]> EntryBlocks { get; } = new();
}

/// <summary>A multi-language string table (the "<c>.strtbl</c>" files): one segment per language.</summary>
public sealed class StrtblDocument
{
    /// <summary>The block of string-name ids between the offset table and the first segment.</summary>
    public byte[] NameBlock { get; set; } = Array.Empty<byte>();

    public List<StrtblSegment> Segments { get; } = new();

    /// <summary>Padding bytes (zeros / 0xCD) that followed the last segment in the original file.</summary>
    public byte[] Trailer { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Reads and writes multi-language tables, a port of the original tool's <c>ReadN</c>/<c>WriteN</c>. Every text is
/// followed by the marker <c>00 00 80 3F</c> (the float 1.0). English is stored as UTF-8 and Spanish, French and the two
/// Spanish variants as Latin-1, both padded with zero bytes; every other language is UTF-16. Because the layout is
/// reproduced from the original tool and not from a specification, use <see cref="RoundTripsExactly"/> to make sure a
/// given file is written back byte for byte before allowing edits.
/// </summary>
public static class StrtblCodec
{
    public const int MaxSegments = 14;

    public static readonly string[] LanguageNames =
    {
        "English", "Spanish", "French", "German", "Italian", "Japanese", "Chinese (Traditional)",
        "Chinese (Simplified)", "Korean", "Spanish (Spain)", "Spanish (Mexico)", "Portuguese", "Polish", "Russian",
    };

    private static readonly byte[] Marker = { 0x00, 0x00, 0x80, 0x3F };
    private static readonly UTF8Encoding Utf8 = new(false);

    public static bool IsUnicode(int segmentIndex) => segmentIndex is not (0 or 1 or 2 or 9 or 10);

    private static Encoding EncodingFor(int segmentIndex) =>
        segmentIndex == 0 ? Utf8 : segmentIndex is 1 or 2 or 9 or 10 ? Encoding.Latin1 : Encoding.Unicode;

    public static bool TryRead(byte[] data, out StrtblDocument? document, out string? error)
    {
        document = null;
        error = null;
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 16)
        {
            error = "the file is too small";
            return false;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count == 0 || count > MaxSegments)
        {
            error = $"the segment count ({count}) is not between 1 and {MaxSegments}";
            return false;
        }

        long tableEnd = 4 + 4L * count;
        if (tableEnd + 14 > data.Length)
        {
            error = "the offset table is cut off";
            return false;
        }

        var offsets = new long[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 4 * i));
            if (offsets[i] < tableEnd || offsets[i] + 14 > data.Length || (i > 0 && offsets[i] <= offsets[i - 1]))
            {
                error = $"segment {i + 1} has an invalid offset ({offsets[i]})";
                return false;
            }
        }

        var doc = new StrtblDocument { NameBlock = data.AsSpan((int)tableEnd, (int)(offsets[0] - tableEnd)).ToArray() };

        for (int i = 0; i < count; i++)
        {
            long position = offsets[i];
            long segmentEnd = i + 1 < count ? offsets[i + 1] : data.Length;
            uint stringCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)position));
            uint unknown = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)position + 4));
            position += 8 + 6;

            if (stringCount > (segmentEnd - position) / 8 + 1)
            {
                error = $"segment {i + 1} claims {stringCount:N0} strings, more than it can hold";
                return false;
            }

            var segment = new StrtblSegment { Language = LanguageNames[i], UnknownValue = unknown };
            for (uint s = 0; s < stringCount; s++)
            {
                if (position + 4 > segmentEnd)
                {
                    error = $"string {s + 1} of segment {i + 1} starts outside the segment";
                    return false;
                }

                position += 4; // the stored length is not needed
                int found = data.AsSpan((int)position, (int)(segmentEnd - position)).IndexOf(Marker);
                if (found < 0)
                {
                    error = $"string {s + 1} of segment {i + 1} has no end marker";
                    return false;
                }

                long end = position + found;
                segment.Strings.Add(Decode(i, data.AsSpan((int)position, found)));

                byte[] block = new byte[20];
                int available = (int)Math.Min(20, data.Length - end);
                data.AsSpan((int)end, available).CopyTo(block);
                segment.EntryBlocks.Add(block);
                position = end + 20;
            }

            doc.Segments.Add(segment);
        }

        document = doc;
        return true;
    }

    private static string Decode(int segmentIndex, ReadOnlySpan<byte> raw)
    {
        Encoding encoding = EncodingFor(segmentIndex);
        if (IsUnicode(segmentIndex))
        {
            return encoding.GetString(raw);
        }

        // Faithful to the original: drop the zero padding that follows each character.
        var bytes = new List<byte>(raw.ToArray());
        int i = 0;
        while (i < bytes.Count - 1)
        {
            if (bytes[i] != 0 && bytes[i + 1] != 0)
            {
                i++;
            }
            else
            {
                bytes.RemoveAt(i + 1);
            }
        }

        return encoding.GetString(bytes.ToArray());
    }

    /// <summary>Returns a message when a text cannot be stored in its language's encoding, otherwise <c>null</c>.</summary>
    public static string? Validate(StrtblDocument document)
    {
        for (int i = 0; i < document.Segments.Count; i++)
        {
            if (IsUnicode(i))
            {
                continue;
            }

            StrtblSegment segment = document.Segments[i];
            for (int s = 0; s < segment.Strings.Count; s++)
            {
                foreach (char c in segment.Strings[s] ?? string.Empty)
                {
                    if (c > 0xFF)
                    {
                        return $"{segment.Language} #{s}: the character U+{(int)c:X4} cannot be stored in this language. Use a Unicode language slot (for example German, Italian, Polish or Russian).";
                    }
                }
            }
        }

        return null;
    }

    public static byte[] Write(StrtblDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var b = new GrowBuffer(littleEndian: true);
        int count = document.Segments.Count;
        b.U32((uint)count);
        b.Fill(0, 4 * count);
        b.Bytes(document.NameBlock);

        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            StrtblSegment segment = document.Segments[i];
            offsets[i] = b.Length;
            b.U32((uint)segment.Strings.Count);
            b.U32(segment.UnknownValue);
            b.U32(1);
            b.U16(0);

            for (int s = 0; s < segment.Strings.Count; s++)
            {
                string text = segment.Strings[s] ?? string.Empty;
                b.U32((uint)(text.Length + 1));
                WriteText(b, text, i);
                if (s < segment.Strings.Count - 1)
                {
                    b.Bytes(segment.EntryBlocks[s]);
                }
            }

            b.U32(0x3F800000);
            b.U32(0x3F800000);
            b.U16(0);
        }

        for (int i = 0; i < count; i++)
        {
            b.PatchU32(4 + 4 * i, (uint)offsets[i]);
        }

        b.Bytes(document.Trailer);
        return b.ToArray();
    }

    private static void WriteText(GrowBuffer b, string text, int segmentIndex)
    {
        if (IsUnicode(segmentIndex))
        {
            b.Bytes(Encoding.Unicode.GetBytes(text));
            return;
        }

        var bytes = new byte[text.Length * 2 + 2];
        for (int i = 0; i < text.Length; i++)
        {
            bytes[2 * i] = (byte)text[i];
        }

        b.Bytes(bytes);
    }

    /// <summary>
    /// True when writing the document reproduces <paramref name="original"/> exactly (allowing only zero / 0xCD padding at
    /// the end, which is then remembered in <see cref="StrtblDocument.Trailer"/>). Only then is editing safe.
    /// </summary>
    public static bool RoundTripsExactly(byte[] original, StrtblDocument document)
    {
        document.Trailer = Array.Empty<byte>();
        byte[] written = Write(document);
        if (written.Length > original.Length || !original.AsSpan(0, written.Length).SequenceEqual(written))
        {
            return false;
        }

        ReadOnlySpan<byte> rest = original.AsSpan(written.Length);
        foreach (byte value in rest)
        {
            if (value != 0 && value != 0xCD)
            {
                return false;
            }
        }

        document.Trailer = rest.ToArray();
        return true;
    }
}
