using System.Buffers.Binary;

namespace MagicRDR.Core.Formats;

/// <summary>
/// Packs raw resource data into a resource file again (RSC05 header + Zstandard body). Only the Switch layout is
/// supported, the mirror of <see cref="RpfResourceUnpacker"/>. The caller decides the data length: it must already be a
/// size that <see cref="Rsc05.IsRepresentable"/> accepts (the writers pad with 0xCD, like the original tool).
/// </summary>
public static class RpfResourcePacker
{
    public const int DefaultZstdLevel = 3;

    public static async Task<byte[]> PackAsync(
        byte[] data,
        int resourceType,
        RpfPlatform platform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (platform != RpfPlatform.Switch)
        {
            throw new NotSupportedException($"Packing {platform} resources is not supported yet (only Switch / Zstandard).");
        }

        if (!Rsc05.IsRepresentable(data.Length))
        {
            throw new ArgumentException($"{data.Length} bytes cannot be described by RSC05 flags; pad the data with Rsc05.RoundUpRepresentable first.", nameof(data));
        }

        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), RpfResourceInfo.Rsc05Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), resourceType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), Rsc05.Flags(data.Length, 0));

        using var output = new MemoryStream(data.Length / 2 + 64);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await RpfCompression.ZstdCompressAsync(new MemoryStream(data, writable: false), output, DefaultZstdLevel, null, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}
