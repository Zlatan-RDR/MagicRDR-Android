namespace MagicRDR.Core.Formats;

/// <summary>
/// The "compact size" encoding of RSC05 resource flags: an 11-bit mantissa and a 4-bit shift, in units of 256 bytes
/// (<c>size = mantissa &lt;&lt; (shift + 8)</c>). Ported from the original tool's <c>GetCompactSize</c>/<c>GetFlags</c>;
/// example: a size of 8192 gives <c>0x810</c>, and a resource with only a virtual part gets flags <c>0xC0000810</c>.
/// </summary>
public static class Rsc05
{
    private const int MantissaMask = 0x7FF;

    public static int CompactSize(int size)
    {
        int mantissa = size >> 8;
        int shift = 0;
        while (mantissa % 2 == 0 && mantissa >= 32 && shift < 15)
        {
            mantissa >>= 1;
            shift++;
        }

        return (shift << 11) | mantissa;
    }

    public static int DecodeSize(int compact) => (compact & MantissaMask) << (((compact >> 11) & 15) + 8);

    /// <summary>True when <paramref name="size"/> survives the compact encoding unchanged.</summary>
    public static bool IsRepresentable(int size) =>
        size > 0 && size % 256 == 0 && (CompactSize(size) & ~0x7FFF) == 0 && DecodeSize(CompactSize(size)) == size;

    /// <summary>The smallest multiple of <paramref name="multiple"/> that is at least <paramref name="size"/> and is representable.</summary>
    public static int RoundUpRepresentable(int size, int multiple)
    {
        if (multiple <= 0 || multiple % 256 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiple), "The multiple must be a positive multiple of 256.");
        }

        long candidate = Math.Max(size, 1);
        candidate = (candidate + multiple - 1) / multiple * multiple;
        while (!IsRepresentable((int)candidate))
        {
            candidate += multiple;
            if (candidate > int.MaxValue / 2)
            {
                throw new InvalidOperationException("The data is too large for a single RSC05 resource.");
            }
        }

        return (int)candidate;
    }

    /// <summary>Flags word for a resource with the given virtual and physical sizes (both must be representable).</summary>
    public static uint Flags(int virtualSize, int physicalSize)
    {
        uint virtualPart = (uint)CompactSize(virtualSize) & 0x7FFF;
        uint physicalPart = physicalSize == 0 ? 0u : (uint)CompactSize(physicalSize) & 0x7FFF;
        return virtualPart | (physicalPart << 15) | (3u << 30);
    }
}
