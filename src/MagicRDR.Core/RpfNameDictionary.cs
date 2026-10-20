using System.Text;

namespace MagicRDR.Core;

/// <summary>Maps name hashes back to known file names (the original tool's <c>ImportedFileNames.txt</c>).</summary>
public sealed class RpfNameDictionary
{
    private Dictionary<uint, string> _names = new();

    // Names added after MarkBaseline(): the only ones SaveAsync has to remember, because the bundled list is already in the app.
    private List<string>? _added;

    public int Count => _names.Count;

    public IEnumerable<string> Names => _names.Values;

    /// <returns><c>true</c> if the name was new.</returns>
    public bool Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        if (_names.TryAdd(RpfHash.GetHash(name), name))
        {
            _added?.Add(name);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Call once, right after the bundled names list has been loaded. From then on <see cref="SaveAsync"/> writes only the names
    /// added later (imported or found by the user), so the big bundled list is never copied into app storage or read twice.
    /// </summary>
    public void MarkBaseline() => _added ??= new List<string>();

    /// <summary>
    /// Loads one name per line. Returns how many new names were added.
    /// Reading, trimming and hashing a big list takes a few hundred milliseconds, so it runs off the calling thread into a
    /// private dictionary; the result is published afterwards on the caller's own thread, which means the UI never reads a
    /// half-filled dictionary and never freezes while a large list loads.
    /// </summary>
    public async Task<int> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Dictionary<uint, string> staged = await Task.Run(() => ParseNames(stream, cancellationToken), cancellationToken);
        return Publish(staged);
    }

    private static Dictionary<uint, string> ParseNames(Stream stream, CancellationToken cancellationToken)
    {
        var staged = new Dictionary<uint, string>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16, leaveOpen: true);

        int sinceCheck = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            sinceCheck++;
            if ((sinceCheck & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue; // blank line or comment
            }

            staged.TryAdd(RpfHash.GetHash(trimmed), trimmed);
        }

        return staged;
    }

    private int Publish(Dictionary<uint, string> staged)
    {
        if (staged.Count == 0)
        {
            return 0;
        }

        if (_names.Count == 0 && _added == null)
        {
            _names = staged; // first list: take it as it is, no copying
            return staged.Count;
        }

        int added = 0;
        foreach (KeyValuePair<uint, string> pair in staged)
        {
            if (_names.TryAdd(pair.Key, pair.Value))
            {
                _added?.Add(pair.Value);
                added++;
            }
        }

        return added;
    }

    /// <summary>Writes the names to remember: only those added after <see cref="MarkBaseline"/>, or every name if it was never called.</summary>
    public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        IEnumerable<string> source = _added ?? (IEnumerable<string>)_names.Values;
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true);
        foreach (string name in source)
        {
            await writer.WriteLineAsync(name.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    public bool Contains(uint hash) => _names.ContainsKey(hash);

    public bool TryGetName(uint hash, out string name)
    {
        if (_names.TryGetValue(hash, out string? value))
        {
            name = value;
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>Returns the known name, or <c>0xXXXXXXXX</c> when the hash is unknown.</summary>
    public string Resolve(uint hash) => _names.TryGetValue(hash, out string? name) ? name : RpfHash.FormatHexHash(hash);
}
