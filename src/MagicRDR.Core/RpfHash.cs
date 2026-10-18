namespace MagicRDR.Core;

/// <summary>
/// The Jenkins one-at-a-time hash RPF6 uses instead of storing file names.
/// Ported from <c>DataUtils.GetHash</c>.
/// </summary>
public static class RpfHash
{
    /// <summary>Hashes a name (case-insensitive, culture-invariant).</summary>
    public static uint GetHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // ToLowerInvariant on purpose: the legacy code used ToLower(), which is culture
        // sensitive and gives different hashes on e.g. a Turkish-locale Android device.
        string text = value.ToLowerInvariant();

        unchecked
        {
            uint hash = 0;
            foreach (char c in text)
            {
                hash += c;
                hash += hash << 10;
                hash ^= hash >> 6;
            }

            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
            return hash;
        }
    }

    /// <summary>Hash of a span, without allocating (same result as <see cref="GetHash(string)"/> for ordinary names).</summary>
    public static uint GetHash(ReadOnlySpan<char> value) => Finish(Accumulate(0, value));

    /// <summary>Feeds characters into a running hash. Lets a caller hash <c>name + extension</c> without building the string.</summary>
    public static uint Accumulate(uint state, ReadOnlySpan<char> text)
    {
        unchecked
        {
            uint hash = state;
            foreach (char c in text)
            {
                hash += char.ToLowerInvariant(c);
                hash += hash << 10;
                hash ^= hash >> 6;
            }

            return hash;
        }
    }

    /// <summary>Applies the final mixing step to a running hash.</summary>
    public static uint Finish(uint state)
    {
        unchecked
        {
            uint hash = state;
            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
            return hash;
        }
    }

    /// <summary>Formats a hash the way unresolved entries are displayed: <c>0x1A2B3C4D</c>.</summary>
    public static string FormatHexHash(uint hash) => $"0x{hash:X8}";
}
