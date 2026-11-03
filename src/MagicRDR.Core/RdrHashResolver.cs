using System.Globalization;

namespace MagicRDR.Core;

/// <summary>An entry of the open archive whose name is still unknown (shown as <c>0x…</c>).</summary>
public readonly record struct RdrHashTarget(uint Hash, bool IsDirectory, bool IsResource, byte ResourceType);

/// <summary>An entry of the open archive whose name is already known. Its name and kind teach the resolver what names in this archive look like.</summary>
public readonly record struct RdrKnownEntry(string Name, bool IsDirectory, bool IsResource, byte ResourceType);

/// <summary>A name that was generated and whose hash matched an unnamed entry.</summary>
public readonly record struct RdrResolvedName(uint Hash, string Name);

/// <summary>Limits of one resolving run. The defaults suit a phone; tests use smaller values.</summary>
public sealed record RdrResolveOptions
{
    /// <summary>An extension from the names list is only tried when at least this many names use it.</summary>
    public int MinExtensionCount { get; init; } = 25;

    public int MaxExtensions { get; init; } = 48;

    /// <summary>A prefix or suffix from the whole names list is only tried when at least this many names use it (those of the open archive always count).</summary>
    public int MinTokenCount { get; init; } = 50;

    public int MaxPrefixes { get; init; } = 24;

    public int MaxSuffixes { get; init; } = 24;

    /// <summary>Numbered names (<c>scene_01</c>, <c>scene_02</c>…) are tried from 0 up to this number.</summary>
    public int MaxNumber { get; init; } = 400;

    /// <summary>Names found in one round feed the next round, up to this many rounds.</summary>
    public int MaxRounds { get; init; } = 3;

    /// <summary>Upper limit of generated names that are hashed, so a run always ends.</summary>
    public long MaxCandidates { get; init; } = 80_000_000;
}

/// <param name="Found">Names whose hash matched, one per hash.</param>
/// <param name="CandidatesTested">How many generated names were hashed.</param>
/// <param name="Unresolved">How many different hashes are still unnamed.</param>
/// <param name="Cancelled">True when the run was stopped early; <paramref name="Found"/> still holds what was found before.</param>
public sealed record RdrResolveResult(IReadOnlyList<RdrResolvedName> Found, long CandidatesTested, int Unresolved, bool Cancelled);

/// <summary>
/// Finds the real names behind hashed entries (<c>0x00FEB5CC</c>). A hash cannot be reversed, so this generates likely names
/// from the names that are already known and keeps the ones whose <see cref="RpfHash"/> matches an unnamed entry:
/// <list type="number">
/// <item>every known name's stem with the common extensions (the same file under another extension);</item>
/// <item>numbered series around the known names (<c>scene_01</c> and <c>scene_03</c> suggest <c>scene_02</c>);</item>
/// <item>the known prefixes and suffixes of this archive swapped between its names (<c>cutscene_x</c> suggests <c>streamed_x</c>).</item>
/// </list>
/// A 32-bit hash can collide, so a match is checked against the kind of the entry: folders take names without an extension,
/// files take names with one, and a resource only takes extensions that other resources of the same type have in this archive.
/// Pure logic: no archive access, no UI.
/// </summary>
public static class RdrHashResolver
{
    public static RdrResolveResult Resolve(
        IReadOnlyCollection<RdrHashTarget> targets,
        IReadOnlyCollection<string> knownNames,
        IReadOnlyCollection<RdrKnownEntry> localEntries,
        RdrResolveOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(knownNames);
        ArgumentNullException.ThrowIfNull(localEntries);

        return new Search(targets, knownNames, localEntries, options ?? new RdrResolveOptions(), progress, cancellationToken).Run();
    }

    private sealed class Search
    {
        private const int BloomBits = 22;
        private const uint BloomMask = (1u << BloomBits) - 1;

        private readonly IReadOnlyCollection<string> _knownNames;
        private readonly IReadOnlyCollection<RdrKnownEntry> _local;
        private readonly RdrResolveOptions _options;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _token;

        private readonly Dictionary<uint, List<RdrHashTarget>> _pending = new();

        // A small bit table of the wanted hashes: almost every candidate is rejected here without a dictionary lookup.
        private readonly ulong[] _bloom = new ulong[1 << (BloomBits - 6)];

        private readonly List<RdrResolvedName> _found = new();
        private readonly Dictionary<byte, HashSet<string>> _typeExtensions = new();
        private readonly HashSet<string> _seenSeries = new(StringComparer.Ordinal);

        private List<string> _extensions = new();
        private List<string> _localExtensions = new();
        private List<string> _prefixes = new();
        private List<string> _suffixes = new();

        private int _pendingFiles;
        private int _pendingDirectories;
        private long _tested;

        public Search(
            IReadOnlyCollection<RdrHashTarget> targets,
            IReadOnlyCollection<string> knownNames,
            IReadOnlyCollection<RdrKnownEntry> localEntries,
            RdrResolveOptions options,
            IProgress<double>? progress,
            CancellationToken token)
        {
            _knownNames = knownNames;
            _local = localEntries;
            _options = options;
            _progress = progress;
            _token = token;

            foreach (RdrHashTarget target in targets)
            {
                if (!_pending.TryGetValue(target.Hash, out List<RdrHashTarget>? list))
                {
                    list = new List<RdrHashTarget>(1);
                    _pending[target.Hash] = list;
                    int slot = (int)(target.Hash & BloomMask);
                    _bloom[slot >> 6] |= 1UL << (slot & 63);
                }

                list.Add(target);
                if (target.IsDirectory)
                {
                    _pendingDirectories++;
                }
                else
                {
                    _pendingFiles++;
                }
            }
        }

        private bool IsDone => _pending.Count == 0 || _tested >= _options.MaxCandidates || _token.IsCancellationRequested;

        public RdrResolveResult Run()
        {
            if (_pending.Count > 0 && !_token.IsCancellationRequested)
            {
                BuildTables(out HashSet<string> dictionaryStems, out List<string> localNames);

                Report(0);
                SearchDictionaryStems(dictionaryStems);
                Report(0.6);

                var frontier = new List<string>(localNames);
                foreach (RdrResolvedName name in _found)
                {
                    frontier.Add(name.Name);
                }

                int seen = _found.Count;

                for (int round = 1; round <= _options.MaxRounds && !IsDone && frontier.Count > 0; round++)
                {
                    if (round > 1)
                    {
                        // Names found in the previous round: the same stem under every common extension.
                        foreach (string name in frontier)
                        {
                            SplitName(name, out string stem, out _);
                            ProbeStem(stem, _extensions);
                        }
                    }

                    SearchNumbered(frontier);
                    SearchVariants(frontier);

                    frontier = _found.Skip(seen).Select(f => f.Name).ToList();
                    seen = _found.Count;
                    Report(0.6 + 0.4 * round / _options.MaxRounds);
                }
            }

            Report(1);
            return new RdrResolveResult(_found, _tested, _pending.Count, _token.IsCancellationRequested);
        }

        // ------------------------------------------------------------------ tables

        private void BuildTables(out HashSet<string> dictionaryStems, out List<string> localNames)
        {
            var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var prefixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var suffixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            dictionaryStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in _knownNames)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                SplitName(name, out string stem, out string ext);
                dictionaryStems.Add(stem);
                if (ext.Length > 0)
                {
                    Increment(extensionCounts, ext);
                }

                CountTokens(stem, prefixCounts, suffixCounts);
            }

            var localExtensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var localPrefixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var localSuffixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            localNames = new List<string>(_local.Count);

            foreach (RdrKnownEntry entry in _local)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                localNames.Add(entry.Name);
                SplitName(entry.Name, out string stem, out string ext);
                CountTokens(stem, localPrefixCounts, localSuffixCounts);

                if (ext.Length == 0 || entry.IsDirectory)
                {
                    continue;
                }

                Increment(localExtensionCounts, ext);
                if (entry.IsResource)
                {
                    if (!_typeExtensions.TryGetValue(entry.ResourceType, out HashSet<string>? allowed))
                    {
                        allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _typeExtensions[entry.ResourceType] = allowed;
                    }

                    allowed.Add(ext);
                }
            }

            _extensions = Top(localExtensionCounts, extensionCounts, _options.MaxExtensions, _options.MinExtensionCount);
            _localExtensions = localExtensionCounts
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(12)
                .Select(p => p.Key)
                .ToList();
            _prefixes = Top(localPrefixCounts, prefixCounts, _options.MaxPrefixes, _options.MinTokenCount);
            _suffixes = Top(localSuffixCounts, suffixCounts, _options.MaxSuffixes, _options.MinTokenCount);
        }

        /// <summary>The most used local entries first, then the most used ones of the whole list (only those that are common enough).</summary>
        private static List<string> Top(Dictionary<string, int> local, Dictionary<string, int> global, int max, int minGlobal)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, int> pair in local.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(max))
            {
                if (seen.Add(pair.Key))
                {
                    result.Add(pair.Key);
                }
            }

            foreach (KeyValuePair<string, int> pair in global.Where(p => p.Value >= minGlobal).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(max))
            {
                if (seen.Add(pair.Key))
                {
                    result.Add(pair.Key);
                }
            }

            return result;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }

        private static void CountTokens(string stem, Dictionary<string, int> prefixes, Dictionary<string, int> suffixes)
        {
            int prefixEnd = PrefixEnd(stem);
            if (prefixEnd > 0)
            {
                Increment(prefixes, stem.Substring(0, prefixEnd).ToLowerInvariant());
            }

            int suffixStart = SuffixStart(stem);
            if (suffixStart > 0)
            {
                Increment(suffixes, stem.Substring(suffixStart).ToLowerInvariant());
            }
        }

        // ------------------------------------------------------------------ name pieces

        /// <summary>Splits "name.ext" into "name" and ".ext" (lower case). No extension gives an empty one.</summary>
        private static void SplitName(string name, out string stem, out string ext)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1)
            {
                stem = name;
                ext = string.Empty;
                return;
            }

            stem = name.Substring(0, dot);
            ext = name.Substring(dot).ToLowerInvariant();
        }

        /// <summary>Index just after the first underscore ("cutscene_" gives 9), or -1 when there is no usable prefix.</summary>
        private static int PrefixEnd(string stem)
        {
            int index = stem.IndexOf('_');
            return index > 0 && index < stem.Length - 1 && index <= 20 ? index + 1 : -1;
        }

        /// <summary>Index of the last underscore when what follows is a short, non-numeric tail ("_lod"), otherwise -1.</summary>
        private static int SuffixStart(string stem)
        {
            int index = stem.LastIndexOf('_');
            if (index <= 0 || index >= stem.Length - 1 || stem.Length - index > 13)
            {
                return -1;
            }

            for (int i = index + 1; i < stem.Length; i++)
            {
                if (!char.IsAsciiDigit(stem[i]))
                {
                    return index;
                }
            }

            return -1;   // a purely numeric tail is handled by the numbered search
        }

        // ------------------------------------------------------------------ searching

        /// <summary>Every stem of the names list under every common extension.</summary>
        private void SearchDictionaryStems(HashSet<string> stems)
        {
            int count = 0;
            foreach (string stem in stems)
            {
                if ((++count & 0x3FFF) == 0)
                {
                    if (IsDone)
                    {
                        return;
                    }

                    Report(0.6 * count / stems.Count);
                }

                ProbeStem(stem, _extensions);
            }
        }

        /// <summary>Numbered series: scene_01 and scene_03 suggest scene_00 … scene_400, written with the same number of digits.</summary>
        private void SearchNumbered(List<string> names)
        {
            List<string> extensions = _localExtensions.Count > 0 ? _localExtensions : _extensions;

            foreach (string name in names)
            {
                if (IsDone)
                {
                    return;
                }

                SplitName(name, out string stem, out _);

                int end = stem.Length;
                while (end > 0 && !char.IsAsciiDigit(stem[end - 1]))
                {
                    end--;
                }

                if (end == 0)
                {
                    continue;
                }

                int start = end;
                while (start > 0 && char.IsAsciiDigit(stem[start - 1]))
                {
                    start--;
                }

                int width = end - start;
                if (width > 4)
                {
                    continue;   // a long digit run is an id, not a counter
                }

                string head = stem.Substring(0, start);
                string tail = stem.Substring(end);
                if (!_seenSeries.Add($"{head}\u0001{tail}\u0001{width}"))
                {
                    continue;   // another member of the same series was already expanded
                }

                for (int number = 0; number <= _options.MaxNumber; number++)
                {
                    if ((number & 63) == 0 && IsDone)
                    {
                        return;
                    }

                    string plain = number.ToString(CultureInfo.InvariantCulture);
                    ProbeStem(head + plain + tail, extensions);
                    if (width > plain.Length)
                    {
                        ProbeStem(head + plain.PadLeft(width, '0') + tail, extensions);
                    }
                }
            }
        }

        /// <summary>Known prefixes put in front of the other names, and known suffixes put behind them.</summary>
        private void SearchVariants(List<string> names)
        {
            var rests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in names)
            {
                SplitName(name, out string stem, out _);

                int prefixEnd = PrefixEnd(stem);
                rests.Add(prefixEnd > 0 ? stem.Substring(prefixEnd) : stem);

                int suffixStart = SuffixStart(stem);
                roots.Add(suffixStart > 0 ? stem.Substring(0, suffixStart) : stem);
            }

            int count = 0;
            foreach (string rest in rests)
            {
                if ((++count & 0xFF) == 0 && IsDone)
                {
                    return;
                }

                ProbeStem(rest, _extensions);
                foreach (string prefix in _prefixes)
                {
                    ProbeStem(prefix + rest, _extensions);
                }
            }

            foreach (string root in roots)
            {
                if ((++count & 0xFF) == 0 && IsDone)
                {
                    return;
                }

                foreach (string suffix in _suffixes)
                {
                    ProbeStem(root + suffix, _extensions);
                }
            }
        }

        /// <summary>Hashes the stem once, then finishes the hash for each extension (the stem is not hashed again).</summary>
        private void ProbeStem(string stem, List<string> extensions)
        {
            uint state = RpfHash.Accumulate(0, stem);

            if (_pendingDirectories > 0)
            {
                Probe(RpfHash.Finish(state), stem, string.Empty);
            }

            if (_pendingFiles == 0)
            {
                return;
            }

            foreach (string ext in extensions)
            {
                Probe(RpfHash.Finish(RpfHash.Accumulate(state, ext)), stem, ext);
            }
        }

        private void Probe(uint hash, string stem, string ext)
        {
            _tested++;

            int slot = (int)(hash & BloomMask);
            if ((_bloom[slot >> 6] & (1UL << (slot & 63))) != 0 && _pending.ContainsKey(hash))
            {
                Accept(hash, stem + ext);
            }
        }

        private void Accept(uint hash, string name)
        {
            List<RdrHashTarget> targets = _pending[hash];
            SplitName(name, out _, out string ext);

            foreach (RdrHashTarget target in targets)
            {
                if (!IsPlausible(target, ext))
                {
                    continue;
                }

                _found.Add(new RdrResolvedName(hash, name));
                _pending.Remove(hash);
                foreach (RdrHashTarget removed in targets)
                {
                    if (removed.IsDirectory)
                    {
                        _pendingDirectories--;
                    }
                    else
                    {
                        _pendingFiles--;
                    }
                }

                return;
            }
        }

        /// <summary>Guards against chance matches of the 32-bit hash: the name must look like the kind of entry it would name.</summary>
        private bool IsPlausible(RdrHashTarget target, string ext)
        {
            if (target.IsDirectory)
            {
                return ext.Length == 0;
            }

            if (ext.Length == 0)
            {
                return false;
            }

            return !target.IsResource
                || !_typeExtensions.TryGetValue(target.ResourceType, out HashSet<string>? allowed)
                || allowed.Contains(ext);
        }

        private void Report(double fraction) => _progress?.Report(Math.Clamp(fraction, 0, 1));
    }
}
