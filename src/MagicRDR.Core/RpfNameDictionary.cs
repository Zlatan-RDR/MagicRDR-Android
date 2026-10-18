using System.Text;

namespace MagicRDR.Core;

/// <summary>Maps name hashes back to known file names (the original tool's <c>ImportedFileNames.txt</c>).</summary>
public sealed class RpfNameDictionary
{
    private readonly Dictionary<uint, string> _names = new();

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
        return _names.TryAdd(RpfHash.GetHash(name), name);
    }

    /// <summary>Loads one name per line. Returns how many new names were added.</summary>
    public async Task<int> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        int added = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16384, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue; // blank line or comment
            }

            if (Add(trimmed))
            {
                added++;
            }
        }

        return added;
    }

    public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true);
        foreach (string name in _names.Values)
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
