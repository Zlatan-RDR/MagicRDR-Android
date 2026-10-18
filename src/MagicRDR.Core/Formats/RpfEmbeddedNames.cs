using System.Text;

namespace MagicRDR.Core.Formats;

/// <summary>
/// Reads the file names that some archives (the Switch ones) store after their data: a table of
/// (name offset, time) pairs for every entry followed by the NUL-separated names, protected with the same AES scheme as the
/// table of contents. Ported from the original tool's <c>ReadStringTable</c>. Every candidate name is accepted only when its
/// hash equals the hash of an entry that has no name yet, so a wrong guess about the layout can never produce a wrong name.
/// </summary>
public static class RpfEmbeddedNames
{
    private const int MaxNameLength = 260;

    public static async Task<int> LoadAsync(
        Stream source,
        int debugDataOffset,
        int entryCount,
        ISet<uint> unresolvedHashes,
        RpfNameDictionary names,
        byte[] key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(unresolvedHashes);
        ArgumentNullException.ThrowIfNull(names);

        long offset = (long)debugDataOffset * 8;
        if (debugDataOffset <= 0 || entryCount <= 0 || offset <= 16 || offset >= source.Length || unresolvedHashes.Count == 0)
        {
            return 0;
        }

        long wanted = Math.Min(source.Length - offset, (long)entryCount * 400 + 65536);
        byte[] buffer = new byte[wanted];
        source.Position = offset;
        await source.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        foreach (bool decrypt in new[] { true, false })
        {
            byte[] data = decrypt ? await Task.Run(() => RpfCrypto.Decrypt(buffer, key), cancellationToken).ConfigureAwait(false) : buffer;
            int added = Scan(data, entryCount * 8, unresolvedHashes, names);
            if (added > 0)
            {
                return added;
            }
        }

        return 0;
    }

    private static int Scan(byte[] data, int start, ISet<uint> unresolved, RpfNameDictionary names)
    {
        int added = 0;
        int position = Math.Max(0, start);
        while (position < data.Length)
        {
            int end = Array.IndexOf(data, (byte)0, position);
            if (end < 0)
            {
                end = data.Length;
            }

            int length = end - position;
            if (length is > 0 and <= MaxNameLength && IsPrintable(data.AsSpan(position, length)))
            {
                string name = Encoding.ASCII.GetString(data, position, length);
                if (unresolved.Contains(RpfHash.GetHash(name)) && names.Add(name))
                {
                    added++;
                }
            }

            position = end + 1;
        }

        return added;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> text)
    {
        foreach (byte b in text)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
