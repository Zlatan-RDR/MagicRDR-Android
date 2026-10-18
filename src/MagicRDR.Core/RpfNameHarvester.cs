using System.Text;

namespace MagicRDR.Core;

public sealed record RpfHarvestResult(int Found, int Unresolved, int FilesScanned);

/// <summary>
/// RPF6 stores only name hashes, and a hash cannot be turned back into a name. What <i>can</i> be done is the reverse:
/// read the plain-text files inside the archive (XML, txt, csv…), treat every word as a candidate name, hash it, and keep
/// it only if the hash equals the hash of an entry that has no name yet. A name found this way is therefore verified,
/// apart from the rare chance (32-bit hash) of an accidental match.
/// </summary>
public static class RpfNameHarvester
{
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MinWord = 2;
    private const int MaxWord = 64;

    public static async Task<RpfHarvestResult> HarvestAsync(
        Rpf6Archive archive,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var unresolved = new HashSet<uint>();
        foreach (RpfNode node in archive.EnumerateAll())
        {
            if (!node.IsRoot && !archive.Names.Contains(node.NameHash))
            {
                unresolved.Add(node.NameHash);
            }
        }

        if (unresolved.Count == 0)
        {
            progress?.Report(new RpfProgress(1.0, "Every entry already has a name"));
            return new RpfHarvestResult(0, 0, 0);
        }

        List<RpfNode> candidates = archive.EnumerateAll()
            .Where(n => !n.IsDirectory && !n.IsResource && n.UncompressedSize > 0 && n.UncompressedSize <= MaxFileBytes)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int found = 0;
        int scanned = 0;

        for (int i = 0; i < candidates.Count && unresolved.Count > 0; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RpfNode node = candidates[i];
            progress?.Report(new RpfProgress((double)i / candidates.Count, $"Scanning {node.Name} ({i + 1}/{candidates.Count})"));

            using var buffer = new MemoryStream((int)node.UncompressedSize);
            await archive.ExtractFileAsync(node, buffer, null, cancellationToken).ConfigureAwait(false);

            byte[] data = buffer.GetBuffer();
            int length = (int)buffer.Length;
            if (!LooksLikeText(data, length))
            {
                continue;
            }

            scanned++;
            found += ScanWords(data, length, unresolved, archive.Names, seen);
        }

        progress?.Report(new RpfProgress(1.0, "Name scan finished"));
        return new RpfHarvestResult(found, unresolved.Count, scanned);
    }

    private static bool LooksLikeText(byte[] data, int length)
    {
        int sample = Math.Min(length, 4096);
        if (sample == 0)
        {
            return false;
        }

        int printable = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 0)
            {
                return false;
            }

            if ((b >= 0x20 && b < 0x7F) || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n')
            {
                printable++;
            }
        }

        return printable >= sample * 0.9;
    }

    private static bool IsWordByte(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_' || b == (byte)'-' || b == (byte)'.';

    private static int ScanWords(byte[] data, int length, HashSet<uint> unresolved, RpfNameDictionary names, HashSet<string> seen)
    {
        int found = 0;
        int start = -1;
        for (int i = 0; i <= length; i++)
        {
            bool inWord = i < length && IsWordByte(data[i]);
            if (inWord)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                int wordLength = i - start;
                if (wordLength >= MinWord && wordLength <= MaxWord)
                {
                    string word = Encoding.ASCII.GetString(data, start, wordLength);
                    if (seen.Add(word))
                    {
                        found += TryWord(word, unresolved, names);
                    }
                }

                start = -1;
            }
        }

        return found;
    }

    private static int TryWord(string word, HashSet<uint> unresolved, RpfNameDictionary names)
    {
        int found = 0;
        if (Accept(word, RpfHash.GetHash(word.AsSpan()), unresolved, names))
        {
            found++;
        }

        // "strings.txt.bak" -> also try "strings.txt" and "strings"; a bare word gets every known extension appended.
        int lastDot = word.LastIndexOf('.');
        int firstDot = word.IndexOf('.');
        if (lastDot > 0 && Accept(word.Substring(0, lastDot), RpfHash.GetHash(word.AsSpan(0, lastDot)), unresolved, names))
        {
            found++;
        }

        if (firstDot > 0 && firstDot != lastDot && Accept(word.Substring(0, firstDot), RpfHash.GetHash(word.AsSpan(0, firstDot)), unresolved, names))
        {
            found++;
        }

        found += TryExtensions(word, word.Length, unresolved, names);
        if (lastDot > 0)
        {
            found += TryExtensions(word, lastDot, unresolved, names);
        }

        if (firstDot > 0 && firstDot != lastDot)
        {
            found += TryExtensions(word, firstDot, unresolved, names);
        }

        return found;
    }

    private static int TryExtensions(string word, int baseLength, HashSet<uint> unresolved, RpfNameDictionary names)
    {
        int found = 0;
        uint state = RpfHash.Accumulate(0, word.AsSpan(0, baseLength));
        foreach (string extension in RpfFileTypes.CandidateExtensions)
        {
            uint hash = RpfHash.Finish(RpfHash.Accumulate(state, extension.AsSpan()));
            if (unresolved.Contains(hash) && Accept(word.Substring(0, baseLength) + extension, hash, unresolved, names))
            {
                found++;
            }
        }

        return found;
    }

    private static bool Accept(string candidate, uint hash, HashSet<uint> unresolved, RpfNameDictionary names)
    {
        if (!unresolved.Remove(hash))
        {
            return false;
        }

        names.Add(candidate);
        return true;
    }
}
