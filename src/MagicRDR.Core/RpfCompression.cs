using System.IO.Compression;

namespace MagicRDR.Core;

/// <summary>
/// Stream-based compression helpers.
/// <para>
/// Plain RPF6 entries use <b>raw deflate</b> (zlib without the 2-byte header / Adler-32, i.e. SharpZipLib's
/// <c>noHeader = true</c>); PS3 resources use zlib <i>with</i> header; Switch/PC entries use Zstandard.
/// Everything here is managed code, so it runs unchanged on Android.
/// </para>
/// </summary>
public static class RpfCompression
{
    public const int DefaultZstdLevel = 3;

    // ---------------------------------------------------------------- deflate / zlib

    /// <summary>Compresses <paramref name="source"/> (from its current position to the end) into <paramref name="destination"/>.</summary>
    /// <param name="zlibHeader"><c>false</c> = raw deflate (RPF6 entries), <c>true</c> = zlib container.</param>
    public static async Task DeflateAsync(
        Stream source,
        Stream destination,
        bool zlibHeader = false,
        CompressionLevel level = CompressionLevel.SmallestSize,
        Action<long>? onInputBytes = null,
        CancellationToken cancellationToken = default)
    {
        Stream target = new LeaveOpenStream(destination);
        await using Stream encoder = zlibHeader
            ? new ZLibStream(target, level, leaveOpen: false)
            : new DeflateStream(target, level, leaveOpen: false);

        await StreamUtil.CopyToEndAsync(source, encoder, onInputBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decompresses <paramref name="compressed"/> into <paramref name="destination"/>.</summary>
    public static async Task InflateAsync(
        Stream compressed,
        Stream destination,
        bool zlibHeader = false,
        Action<long>? onOutputBytes = null,
        CancellationToken cancellationToken = default)
    {
        await using Stream decoder = zlibHeader
            ? new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true)
            : new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true);

        await StreamUtil.CopyToEndAsync(decoder, destination, onOutputBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Small-buffer convenience wrapper.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> input, bool zlibHeader = false, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        using var output = new MemoryStream();
        using (Stream encoder = zlibHeader
                   ? new ZLibStream(output, level, leaveOpen: true)
                   : new DeflateStream(output, level, leaveOpen: true))
        {
            encoder.Write(input);
        }

        return output.ToArray();
    }

    /// <summary>Small-buffer convenience wrapper.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> input, bool zlibHeader = false)
    {
        using var inputStream = new MemoryStream(input.ToArray());
        using Stream decoder = zlibHeader
            ? new ZLibStream(inputStream, CompressionMode.Decompress)
            : new DeflateStream(inputStream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    // ---------------------------------------------------------------- zstandard

    public static async Task ZstdCompressAsync(
        Stream source,
        Stream destination,
        int level = DefaultZstdLevel,
        Action<long>? onInputBytes = null,
        CancellationToken cancellationToken = default)
    {
        await using var encoder = new ZstdSharp.CompressionStream(new LeaveOpenStream(destination), level);
        await StreamUtil.CopyToEndAsync(source, encoder, onInputBytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ZstdDecompressAsync(
        Stream compressed,
        Stream destination,
        Action<long>? onOutputBytes = null,
        CancellationToken cancellationToken = default)
    {
        await using var decoder = new ZstdSharp.DecompressionStream(new LeaveOpenStream(compressed));
        await StreamUtil.CopyToEndAsync(decoder, destination, onOutputBytes, cancellationToken).ConfigureAwait(false);
    }
}
