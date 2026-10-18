using System.Globalization;

namespace MagicRDR.Core;

/// <summary>One archive entry paired with one of the files offered as its replacement.</summary>
public readonly record struct BatchMatch(RpfNode Node, int SourceIndex);

/// <summary>The outcome of pairing replacement files with archive entries.</summary>
/// <param name="Matches">Entries that got exactly one file.</param>
/// <param name="UnmatchedTargets">Entries for which no file was offered.</param>
/// <param name="UnmatchedSources">Indexes of offered files that no entry wanted.</param>
/// <param name="AmbiguousTargets">Entries skipped on purpose because several files (or several entries for one file) fitted.</param>
public sealed record BatchMatchResult(
    IReadOnlyList<BatchMatch> Matches,
    IReadOnlyList<RpfNode> UnmatchedTargets,
    IReadOnlyList<int> UnmatchedSources,
    IReadOnlyList<RpfNode> AmbiguousTargets);

/// <summary>
/// Pairs replacement files with archive entries by name, for the batch Replace.
/// A file fits an entry when, in this order of preference, its name equals the entry's name, its name without the extension
/// equals the entry's name without the extension, or its name (with or without extension) is the entry's hash written as
/// <c>0x1A2B3C4D</c>. Comparisons ignore case. Anything that could mean two different pairings is skipped and reported
/// instead of guessed.
/// </summary>
public static class BatchMatcher
{
    public static BatchMatchResult Match(IReadOnlyList<RpfNode> targets, IReadOnlyList<string> sourceNames)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sourceNames);

        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var byStem = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<uint, List<int>>();

        for (int i = 0; i < sourceNames.Count; i++)
        {
            string name = Path.GetFileName(sourceNames[i] ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            string stem = StemOf(name);
            Add(byName, name, i);
            Add(byStem, stem, i);
            if (TryParseHash(name, out uint hash) || TryParseHash(stem, out hash))
            {
                Add(byHash, hash, i);
            }
        }

        var chosen = new List<(RpfNode Node, int Source)>();
        var ambiguous = new List<RpfNode>();
        var unmatched = new List<RpfNode>();

        foreach (RpfNode node in targets)
        {
            string name = node.Name;
            List<int>? candidates = null;
            if (!byName.TryGetValue(name, out candidates) && !byStem.TryGetValue(StemOf(name), out candidates))
            {
                byHash.TryGetValue(node.NameHash, out candidates);
            }

            if (candidates == null || candidates.Count == 0)
            {
                unmatched.Add(node);
            }
            else if (candidates.Count == 1)
            {
                chosen.Add((node, candidates[0]));
            }
            else
            {
                ambiguous.Add(node);
            }
        }

        // A file that two entries want is just as ambiguous: do not pick one of them for it.
        var claims = new Dictionary<int, int>();
        foreach ((RpfNode _, int source) in chosen)
        {
            claims[source] = claims.GetValueOrDefault(source) + 1;
        }

        var matches = new List<BatchMatch>(chosen.Count);
        var used = new HashSet<int>();
        foreach ((RpfNode node, int source) in chosen)
        {
            if (claims[source] > 1)
            {
                ambiguous.Add(node);
            }
            else
            {
                matches.Add(new BatchMatch(node, source));
                used.Add(source);
            }
        }

        var unmatchedSources = new List<int>();
        for (int i = 0; i < sourceNames.Count; i++)
        {
            if (!used.Contains(i))
            {
                unmatchedSources.Add(i);
            }
        }

        return new BatchMatchResult(matches, unmatched, unmatchedSources, ambiguous);
    }

    /// <summary>The file name without its last extension ("strings.wst" gives "strings"; "0x00FEB5CC" stays as it is).</summary>
    public static string StemOf(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == 0 ? name : stem;
    }

    private static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, int index)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<int>? list))
        {
            list = new List<int>(1);
            map[key] = list;
        }

        list.Add(index);
    }

    private static bool TryParseHash(string text, out uint hash)
    {
        hash = 0;
        if (text.Length < 3 || text.Length > 10 || text[0] != '0' || (text[1] != 'x' && text[1] != 'X'))
        {
            return false;
        }

        return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash);
    }
}

/// <summary>Chooses file names for a batch extract so two entries with the same name never overwrite each other.</summary>
public static class BatchNaming
{
    /// <summary>Returns <paramref name="name"/>, or <c>name_2.ext</c>, <c>name_3.ext</c>... if it is already in <paramref name="used"/>. The chosen name is added to the set.</summary>
    public static string UniqueName(string name, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "file";
        }

        if (used.Add(name))
        {
            return name;
        }

        string extension = Path.GetExtension(name);
        string stem = name.Substring(0, name.Length - extension.Length);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem}_{n}{extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
