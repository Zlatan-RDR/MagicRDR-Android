using System.Text;

namespace MagicRDR.Core.Formats;

/// <summary>What <see cref="RpfResourceUnpacker.UnpackDetailedAsync"/> found.</summary>
/// <param name="Data">The raw resource data.</param>
/// <param name="ExpectedSize">Total size declared by the resource flags (virtual + physical), or 0 when unknown.</param>
/// <param name="OpenEndedFrames">How many frames had no end marker and were repaired before decoding.</param>
public sealed record RpfUnpackResult(byte[] Data, int ExpectedSize, int OpenEndedFrames);

/// <summary>
/// Unpacks a packed resource file (<c>RSC05/06/85/86</c> header + compressed body) into its raw data.
/// Only the Switch layout (header, then one or more Zstandard frames) is supported. Xbox 360 resources use LZX through a
/// Windows-only native library, and PS3 resources are not verified, so both report <see cref="NotSupportedException"/>.
/// <para>
/// The body is read frame by frame. The exact length of each frame is measured first, so padding after the last frame
/// is ignored. A frame that ends on a block boundary without the "last block" marker (as found in real Switch resources,
/// where a strict decoder reports "Premature end of stream") is repaired and decoded; the result is then checked
/// against the size the resource flags declare, so genuinely missing data is still reported.
/// </para>
/// </summary>
public static class RpfResourceUnpacker
{
    public static async Task<byte[]> UnpackAsync(byte[] resource, RpfPlatform platform, CancellationToken cancellationToken = default) =>
        (await UnpackDetailedAsync(resource, platform, cancellationToken).ConfigureAwait(false)).Data;

    public static async Task<RpfUnpackResult> UnpackDetailedAsync(byte[] resource, RpfPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (platform != RpfPlatform.Switch)
        {
            throw new NotSupportedException($"Unpacking {platform} resources is not supported yet (only Switch / Zstandard).");
        }

        using var input = new MemoryStream(resource, writable: false);
        RpfResourceInfo info = RpfResourceInfo.Read(input, platform);
        if (!info.IsResource)
        {
            throw new InvalidDataException("This file is not a packed resource (no RSC header).");
        }

        bool extended = info.Version is "RSC85" or "RSC86";
        int headerLength = extended ? 16 : 12;
        if (resource.Length <= headerLength)
        {
            throw new InvalidDataException("The resource has no data after its header.");
        }

        if (extended && info.ResourceType == 2)
        {
            throw new NotSupportedException("This resource is encrypted; decrypting resources is not supported yet.");
        }

        using var output = new MemoryStream();
        int position = headerLength;
        int frames = 0;
        int repaired = 0;

        while (resource.Length - position >= 4 && ZstdFrames.IsFrameStart(resource.AsSpan(position)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ZstdFrames.TryReadFrame(resource.AsSpan(position), out ZstdFrame frame, out string? error))
            {
                throw new InvalidDataException($"The Zstandard frame at byte {position} is damaged or cut off: {error}.");
            }

            if (!frame.IsSkippable)
            {
                ReadOnlyMemory<byte> bytes = resource.AsMemory(position, frame.Length);
                if (frame.IsOpenEnded)
                {
                    bytes = ZstdFrames.Terminate(bytes.Span, frame);
                    repaired++;
                }

                using var slice = new MemoryStream(bytes.ToArray(), writable: false);
                await RpfCompression.ZstdDecompressAsync(slice, output, null, cancellationToken).ConfigureAwait(false);
                frames++;
            }

            position += frame.Length;
        }

        if (frames == 0)
        {
            throw new InvalidDataException($"No Zstandard frame was found after the {headerLength}-byte resource header.");
        }

        int expected = ExpectedSize(info);
        if (expected > 0 && output.Length < expected)
        {
            throw new InvalidDataException($"The resource declares {expected:N0} bytes of data but only {output.Length:N0} could be decompressed; its compressed body is incomplete.");
        }

        return new RpfUnpackResult(output.ToArray(), expected, repaired);
    }

    private static int ExpectedSize(RpfResourceInfo info)
    {
        try
        {
            return new RpfFlags(info.Flag1, info.Flag2).GetTotalSize();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// One-line technical summary of a packed resource (header fields, body size, Zstandard frames, trailing bytes),
    /// meant for the viewer's notice line so problems can be diagnosed from a screenshot. Never throws.
    /// </summary>
    public static string Describe(byte[] resource, RpfPlatform platform)
    {
        try
        {
            using var input = new MemoryStream(resource, writable: false);
            RpfResourceInfo info = RpfResourceInfo.Read(input, platform);
            if (!info.IsResource)
            {
                return string.Empty;
            }

            bool extended = info.Version is "RSC85" or "RSC86";
            int headerLength = extended ? 16 : 12;
            var text = new StringBuilder();
            text.Append(info.Version).Append(" type ").Append(info.ResourceType)
                .Append(" flags 0x").Append(((uint)info.Flag1).ToString("X8"));
            if (extended)
            {
                text.Append('/').Append(((uint)info.Flag2).ToString("X8"));
            }

            text.Append(" body ").Append(Math.Max(0, resource.Length - headerLength)).Append(" B");
            int expected = ExpectedSize(info);
            if (expected > 0)
            {
                text.Append(" -> ").Append(expected).Append(" B declared");
            }

            int position = headerLength;
            int count = 0;
            while (resource.Length - position >= 4 && ZstdFrames.IsFrameStart(resource.AsSpan(position)))
            {
                if (!ZstdFrames.TryReadFrame(resource.AsSpan(position), out ZstdFrame frame, out string? error))
                {
                    text.Append("; frame ").Append(count + 1).Append(" at ").Append(position).Append(" is cut off (").Append(error).Append(')');
                    return text.ToString();
                }

                count++;
                text.Append("; frame ").Append(count).Append(": ").Append(frame.Length).Append(" B");
                if (frame.IsOpenEnded)
                {
                    text.Append(" (no end marker)");
                }

                if (frame.ContentSize is { } size)
                {
                    text.Append(" -> ").Append(size).Append(" B");
                }

                position += frame.Length;
            }

            int trailing = resource.Length - position;
            if (count == 0)
            {
                text.Append("; no Zstandard frame at byte ").Append(headerLength);
            }
            else if (trailing > 0)
            {
                text.Append("; ").Append(trailing).Append(" trailing byte(s)");
            }

            return text.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
