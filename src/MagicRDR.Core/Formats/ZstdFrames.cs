using System.Buffers.Binary;

namespace MagicRDR.Core.Formats;

/// <summary>A Zstandard frame found inside a buffer: its total length in bytes and what its header declares.</summary>
/// <param name="IsOpenEnded">
/// True when the data stops exactly at a block boundary and the encoder never wrote the "last block" marker. Some Switch
/// resources are stored like this; the game's streaming decoder does not mind, strict decoders do. <see cref="ZstdFrames.Terminate"/> repairs it.
/// </param>
public readonly record struct ZstdFrame(int Length, long? ContentSize, bool HasChecksum, bool IsSkippable, bool IsOpenEnded = false);

/// <summary>
/// Measures Zstandard frames without decompressing them (RFC 8878: frame header, then blocks, then an optional checksum).
/// Knowing exactly where a frame ends lets resources be unpacked even when padding follows the frame, and gives a precise
/// diagnostic ("block needs N bytes but only M remain") when a frame is cut off.
/// </summary>
public static class ZstdFrames
{
    public const uint Magic = 0xFD2FB528;
    private const uint SkippableBase = 0x184D2A50;

    /// <summary>
    /// Makes an open-ended frame complete by appending an empty "last" raw block (<c>01 00 00</c>) and clearing the
    /// checksum flag (the missing end also lacks the checksum). The decoded content is exactly what was stored.
    /// </summary>
    public static byte[] Terminate(ReadOnlySpan<byte> frameBytes, ZstdFrame frame)
    {
        if (!frame.IsOpenEnded || frame.IsSkippable)
        {
            throw new ArgumentException("Only an open-ended Zstandard frame can be terminated.", nameof(frame));
        }

        byte[] result = new byte[frame.Length + 3];
        frameBytes.Slice(0, frame.Length).CopyTo(result);
        result[4] &= 0xFB;            // Content_Checksum_flag (bit 2)
        result[frame.Length] = 0x01;  // Last_Block = 1, Block_Type = Raw, Block_Size = 0
        return result;
    }

    public static bool IsFrameStart(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return magic == Magic || (magic & 0xFFFFFFF0u) == SkippableBase;
    }

    public static bool TryReadFrame(ReadOnlySpan<byte> data, out ZstdFrame frame, out string? error)
    {
        frame = default;
        error = null;

        if (data.Length < 4)
        {
            error = "fewer than 4 bytes are left";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if ((magic & 0xFFFFFFF0u) == SkippableBase)
        {
            if (data.Length < 8)
            {
                error = "the skippable-frame header is cut off";
                return false;
            }

            long total = 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            if (total > data.Length)
            {
                error = $"a skippable frame needs {total} bytes but only {data.Length} remain";
                return false;
            }

            frame = new ZstdFrame((int)total, null, false, true);
            return true;
        }

        if (magic != Magic)
        {
            error = "the Zstandard magic number is missing";
            return false;
        }

        if (data.Length < 5)
        {
            error = "the frame header is cut off";
            return false;
        }

        byte descriptor = data[4];
        int contentSizeFlag = descriptor >> 6;
        bool singleSegment = (descriptor & 0x20) != 0;
        bool checksum = (descriptor & 0x04) != 0;
        int dictionaryFlag = descriptor & 0x03;
        if ((descriptor & 0x08) != 0)
        {
            error = "a reserved header bit is set";
            return false;
        }

        int position = 5;
        if (!singleSegment)
        {
            position += 1; // window descriptor
        }

        position += dictionaryFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };

        int contentSizeBytes = contentSizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        if (position + contentSizeBytes > data.Length)
        {
            error = "the frame header is cut off";
            return false;
        }

        long? contentSize = null;
        if (contentSizeBytes > 0)
        {
            ulong value = 0;
            for (int i = 0; i < contentSizeBytes; i++)
            {
                value |= (ulong)data[position + i] << (8 * i);
            }

            if (contentSizeBytes == 2)
            {
                value += 256;
            }

            contentSize = (long)value;
        }

        position += contentSizeBytes;

        while (true)
        {
            if (position + 3 > data.Length)
            {
                // Ended exactly on a block boundary (or with 1-2 stray bytes): the "last block" marker was never written.
                frame = new ZstdFrame(position, contentSize, checksum, false, IsOpenEnded: true);
                return true;
            }

            uint header = data[position] | ((uint)data[position + 1] << 8) | ((uint)data[position + 2] << 16);
            bool last = (header & 1) != 0;
            int type = (int)((header >> 1) & 3);
            long size = header >> 3;
            if (type == 3)
            {
                error = $"the block at byte {position} has a reserved type";
                return false;
            }

            long content = type == 1 ? 1 : size; // an RLE block stores a single byte
            position += 3;
            if (position + content > data.Length)
            {
                error = $"the block at byte {position - 3} needs {content} bytes but only {data.Length - position} remain";
                return false;
            }

            position += (int)content;
            if (last)
            {
                break;
            }
        }

        if (checksum)
        {
            if (position + 4 > data.Length)
            {
                error = "the frame checksum is cut off";
                return false;
            }

            position += 4;
        }

        frame = new ZstdFrame(position, contentSize, checksum, false);
        return true;
    }
}
