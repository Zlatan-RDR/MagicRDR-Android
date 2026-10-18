using System.Buffers.Binary;
using System.Globalization;

namespace MagicRDR.Core;

public sealed class RpfHeader
{
    public int EntryCount { get; internal set; }

    public int TocSize { get; internal set; }

    public int DebugDataOffset { get; internal set; }

    public int EncryptionFlag { get; internal set; }

    public bool IsEncrypted => unchecked((uint)EncryptionFlag) != 0;

    /// <summary>20 bytes per entry, rounded up to 16 (AES block size).</summary>
    internal static int ComputeTocSize(int entryCount) => (int)(((long)entryCount * 20 + 15) & ~15L);
}

/// <summary>
/// Reads, edits and writes Red Dead Redemption <c>RPF6</c> archives on any <see cref="Stream"/>.
/// <para>
/// Design notes
/// <list type="bullet">
/// <item>The source stream must be seekable and stays open for the archive's lifetime (file data is read lazily).</item>
/// <item>All source-stream I/O is serialised with an internal lock, so an extract and a save cannot interleave.</item>
/// <item>Edits (<see cref="ReplaceFile"/>, <see cref="AddFile"/>, <see cref="RemoveNode"/>) only change the in-memory tree.
/// <see cref="SaveAsync"/> writes a <i>new</i> archive and does not mutate the model, so the archive stays valid
/// (and can be saved again) afterwards.</item>
/// <item>The tree itself is not thread-safe: mutate it from one thread (typically the UI thread) while no I/O operation is running.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class Rpf6Archive : IDisposable
{
    /// <summary>"RPF6" read as a big-endian int.</summary>
    public const int Magic = 0x52504636;

    internal const int EntrySize = 20;
    internal const int MaxStoredSize = 0x0FFFFFFF;
    private const int MaxEntryCount = 4_000_000;

    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly byte[] _encryptionKey;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    private Rpf6Archive(Stream source, RpfOpenOptions options)
    {
        _source = source;
        _leaveOpen = options.LeaveOpen;
        _encryptionKey = options.EncryptionKey ?? RpfCrypto.DefaultKey;
        Names = options.Names ?? new RpfNameDictionary();
        Platform = options.Platform ?? RpfPlatform.Unknown;
        Header = new RpfHeader();
        Root = null!;
    }

    public RpfHeader Header { get; private set; }

    public RpfPlatform Platform { get; private set; }

    public RpfNode Root { get; private set; }

    public RpfNameDictionary Names { get; }

    /// <summary>How many names were read from the archive itself when it was opened (0 if it stores none).</summary>
    public int EmbeddedNamesFound { get; private set; }

    /// <summary>True once something was replaced, added or removed since the archive was opened.</summary>
    public bool HasPendingChanges { get; private set; }

    /// <summary>Where large temporary buffers (compression during save) go. Defaults to the system temp/cache directory.</summary>
    public string TempDirectory { get; set; } = Path.GetTempPath();

    public int FileCount => EnumerateAll().Count(n => !n.IsDirectory);

    public int DirectoryCount => EnumerateAll().Count(n => n.IsDirectory);

    // ------------------------------------------------------------------ open

    /// <summary>Opens an archive. Header and table of contents are read asynchronously; file data is read on demand.</summary>
    /// <exception cref="InvalidDataException">The stream is not a valid RPF6 archive.</exception>
    public static async Task<Rpf6Archive> OpenAsync(
        Stream source,
        RpfOpenOptions? options = null,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The RPF stream must be readable and seekable.", nameof(source));
        }

        var archive = new Rpf6Archive(source, options ?? new RpfOpenOptions());
        try
        {
            await archive.LoadAsync(progress, cancellationToken).ConfigureAwait(false);
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Convenience overload for a local file (e.g. a working copy in the app's cache directory).
    /// The file is opened read-only with async I/O and closed when the archive is disposed.
    /// </summary>
    public static async Task<Rpf6Archive> OpenFileAsync(
        string path,
        RpfOpenOptions? options = null,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            options ??= new RpfOpenOptions();
            options.LeaveOpen = false;
            return await OpenAsync(stream, options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LoadAsync(IProgress<RpfProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new RpfProgress(0.02, "Reading RPF header"));

        _source.Position = 0;
        byte[] headerBytes = new byte[16];
        await _source.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        int magic = BinaryPrimitives.ReadInt32BigEndian(headerBytes.AsSpan(0, 4));
        if (magic != Magic)
        {
            throw new InvalidDataException("This file is not an RPF6 archive (bad header magic).");
        }

        int entryCount = BinaryPrimitives.ReadInt32BigEndian(headerBytes.AsSpan(4, 4));
        if (entryCount <= 0 || entryCount > MaxEntryCount)
        {
            throw new InvalidDataException($"The RPF header reports an unreasonable entry count ({entryCount}).");
        }

        var header = new RpfHeader
        {
            EntryCount = entryCount,
            TocSize = RpfHeader.ComputeTocSize(entryCount),
            DebugDataOffset = BinaryPrimitives.ReadInt32BigEndian(headerBytes.AsSpan(8, 4)),
            EncryptionFlag = BinaryPrimitives.ReadInt32BigEndian(headerBytes.AsSpan(12, 4)),
        };

        if (Platform == RpfPlatform.Unknown)
        {
            // Same heuristic as the original VerifyGameVersion: no debug-name table => console (Xbox 360 / PS3).
            Platform = header.DebugDataOffset == 0 ? RpfPlatform.Xbox : RpfPlatform.Switch;
        }

        if (16L + header.TocSize > _source.Length)
        {
            throw new InvalidDataException("The table of contents extends past the end of the file.");
        }

        progress?.Report(new RpfProgress(0.10, "Reading table of contents"));
        byte[] toc = new byte[header.TocSize];
        await _source.ReadExactlyAsync(toc, cancellationToken).ConfigureAwait(false);

        byte[] plainToc = toc;
        if (header.IsEncrypted)
        {
            progress?.Report(new RpfProgress(0.25, "Decrypting table of contents"));
            plainToc = await Task.Run(() => RpfCrypto.Decrypt(toc, _encryptionKey), cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new RpfProgress(0.45, "Building file tree"));
        RpfNode[] nodes = await Task.Run(() => ParseToc(plainToc, entryCount), cancellationToken).ConfigureAwait(false);

        Header = header;
        Root = nodes[0];

        // Switch archives keep a names table after the data; names found there are verified by their hash.
        if (header.DebugDataOffset > 0)
        {
            progress?.Report(new RpfProgress(0.8, "Reading file names stored in the archive"));
            var unresolved = new HashSet<uint>();
            for (int i = 1; i < nodes.Length; i++)
            {
                if (!Names.Contains(nodes[i].NameHash))
                {
                    unresolved.Add(nodes[i].NameHash);
                }
            }

            try
            {
                EmbeddedNamesFound = await Formats.RpfEmbeddedNames.LoadAsync(
                    _source, header.DebugDataOffset, entryCount, unresolved, Names, _encryptionKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EmbeddedNamesFound = 0; // names are a convenience; the archive itself is fine
            }
        }

        progress?.Report(new RpfProgress(1.0, "Archive loaded"));
    }

    private RpfNode[] ParseToc(byte[] toc, int count)
    {
        var nodes = new RpfNode[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = toc.AsSpan(i * EntrySize, EntrySize);
            RpfEntry entry;

            // Directories have the top bit of their "content index" field set (0x80 in the first byte).
            if (e[8] == 0x80)
            {
                int unknown = BinaryPrimitives.ReadInt32BigEndian(e.Slice(16, 4));
                if (unknown != 0)
                {
                    throw new InvalidDataException($"Directory entry {i} contains unexpected data.");
                }

                entry = new RpfDirectoryEntry
                {
                    NameOffset = BinaryPrimitives.ReadUInt32BigEndian(e.Slice(0, 4)),
                    Flags = BinaryPrimitives.ReadInt32BigEndian(e.Slice(4, 4)),
                    ContentEntryIndex = BinaryPrimitives.ReadInt32BigEndian(e.Slice(8, 4)) & int.MaxValue,
                    ContentEntryCount = BinaryPrimitives.ReadInt32BigEndian(e.Slice(12, 4)) & 0x0FFFFFFF,
                    Unknown = unknown,
                };
            }
            else
            {
                entry = new RpfFileEntry
                {
                    NameOffset = BinaryPrimitives.ReadUInt32BigEndian(e.Slice(0, 4)),
                    SizeInArchive = BinaryPrimitives.ReadInt32BigEndian(e.Slice(4, 4)) & 0x0FFFFFFF,
                    RawOffset = BinaryPrimitives.ReadUInt32BigEndian(e.Slice(8, 4)),
                    Flags = new RpfFlags(
                        BinaryPrimitives.ReadInt32BigEndian(e.Slice(12, 4)),
                        BinaryPrimitives.ReadInt32BigEndian(e.Slice(16, 4))),
                };
            }

            nodes[i] = new RpfNode(this, entry) { ExistedInArchive = true };
        }

        if (!nodes[0].IsDirectory)
        {
            throw new InvalidDataException("The first TOC entry is not the root directory.");
        }

        for (int i = 0; i < count; i++)
        {
            if (nodes[i].Entry is not RpfDirectoryEntry dir)
            {
                continue;
            }

            long start = dir.ContentEntryIndex;
            long length = dir.ContentEntryCount;
            if (start < 0 || length < 0 || start + length > count)
            {
                throw new InvalidDataException($"Directory entry {i} references children outside the table of contents.");
            }

            for (long j = start; j < start + length; j++)
            {
                RpfNode child = nodes[(int)j];
                child.Parent = nodes[i];
                nodes[i].ChildList.Add(child);
            }
        }

        return nodes;
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>Depth-first enumeration of <paramref name="start"/> and everything below it.</summary>
    public IEnumerable<RpfNode> EnumerateDescendants(RpfNode start)
    {
        ArgumentNullException.ThrowIfNull(start);

        var stack = new Stack<RpfNode>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            RpfNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    public IEnumerable<RpfNode> EnumerateAll() => Root == null ? Enumerable.Empty<RpfNode>() : EnumerateDescendants(Root);

    /// <summary>
    /// Resolves a path such as <c>root/dir/file.txt</c>. Segments may also be raw hashes (<c>0x1A2B3C4D</c>).
    /// </summary>
    public RpfNode? FindByPath(string path, char separator = '/')
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string[] parts = path.TrimEnd(separator).Split(separator);
        if (!string.Equals(parts[0], "root", StringComparison.Ordinal))
        {
            return null;
        }

        RpfNode current = Root;
        for (int i = 1; i < parts.Length; i++)
        {
            uint hash;
            if (parts[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(parts[i].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
            {
                hash = parsed;
            }
            else
            {
                hash = RpfHash.GetHash(parts[i]);
            }

            RpfNode? child = current.FindChild(hash);
            if (child == null)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    // ------------------------------------------------------------------ extraction

    /// <summary>
    /// Writes the file's decompressed content to <paramref name="destination"/>.
    /// Plain compressed files are inflated (deflate, or Zstandard on Switch); resources and
    /// uncompressed files are copied as stored. Content that was replaced but not yet saved is returned as-is.
    /// </summary>
    public async Task ExtractFileAsync(
        RpfNode node,
        Stream destination,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureOwned(node);
        ArgumentNullException.ThrowIfNull(destination);
        if (node.IsDirectory)
        {
            throw new ArgumentException("Use ExtractToDirectoryAsync to extract a directory.", nameof(node));
        }

        var tracker = new ProgressTracker(progress, EstimateOutputSize(node));
        tracker.SetMessage($"Extracting {node.Name}");

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExtractCoreAsync(node, destination, tracker.Advance, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }

        tracker.Complete($"Extracted {node.Name}");
    }

    /// <summary>
    /// Extracts a file, or a whole directory tree, below <paramref name="destinationDirectory"/> using normal
    /// <see cref="System.IO"/> paths. Returns the number of files written.
    /// </summary>
    public async Task<int> ExtractToDirectoryAsync(
        RpfNode node,
        string destinationDirectory,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureOwned(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var items = new List<(RpfNode Node, string RelativePath)>();
        if (node.IsDirectory)
        {
            CollectFiles(node, string.Empty, items);
        }
        else
        {
            items.Add((node, SanitizeSegment(node.Name)));
        }

        long total = 0;
        foreach (var item in items)
        {
            total += EstimateOutputSize(item.Node);
        }

        var tracker = new ProgressTracker(progress, total);
        Directory.CreateDirectory(destinationDirectory);

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int written = 0;
            foreach ((RpfNode fileNode, string relativePath) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracker.SetMessage($"Extracting {fileNode.Name} ({written + 1}/{items.Count})");

                string target = Path.Combine(destinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await ExtractCoreAsync(fileNode, output, tracker.Advance, cancellationToken).ConfigureAwait(false);
                }

                written++;
            }

            tracker.Complete($"Extracted {written} file(s)");
            return written;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task ExtractCoreAsync(RpfNode node, Stream destination, Action<long>? onBytes, CancellationToken cancellationToken)
    {
        RpfFileEntry file = node.AsFile ?? throw new InvalidOperationException("Directories cannot be extracted as a single file.");

        if (node.PendingData != null)
        {
            node.PendingData.Position = 0;
            await StreamUtil.CopyToEndAsync(node.PendingData, destination, onBytes, cancellationToken).ConfigureAwait(false);
            node.PendingData.Position = 0;
            return;
        }

        long offset = file.GetOffset();
        long size = file.SizeInArchive;
        if (offset < 0 || offset + size > _source.Length)
        {
            throw new InvalidDataException($"'{node.Name}' points outside the archive (offset {offset}, size {size}).");
        }

        _source.Position = offset;
        if (file.Flags.IsCompressed && !file.Flags.IsResource)
        {
            var range = new RangeReadStream(_source, size);
            if (Platform == RpfPlatform.Switch)
            {
                await RpfCompression.ZstdDecompressAsync(range, destination, onBytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RpfCompression.InflateAsync(range, destination, zlibHeader: false, onBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await StreamUtil.CopyExactAsync(_source, destination, size, onBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long EstimateOutputSize(RpfNode node) => node.IsDirectory ? 0 : node.UncompressedSize;

    private static void CollectFiles(RpfNode directory, string prefix, List<(RpfNode Node, string RelativePath)> items)
    {
        foreach (RpfNode child in directory.Children)
        {
            string segment = SanitizeSegment(child.Name);
            string relative = prefix.Length == 0 ? segment : Path.Combine(prefix, segment);
            if (child.IsDirectory)
            {
                CollectFiles(child, relative, items);
            }
            else
            {
                items.Add((child, relative));
            }
        }
    }

    /// <summary>Makes a name safe to use as a single path segment (no separators, no ".." traversal).</summary>
    public static string SanitizeSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "_";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ':')
            {
                chars[i] = '_';
            }
        }

        string result = new string(chars).Trim();
        return result is "" or "." or ".." ? "_" : result;
    }

    // ------------------------------------------------------------------ lifetime

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnsureOwned(RpfNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!ReferenceEquals(node.Archive, this))
        {
            throw new ArgumentException("The node belongs to a different archive.", nameof(node));
        }
    }

    /// <summary>Releases the archive, all pending replacement streams and (unless <c>LeaveOpen</c>) the source stream. Do not call while an operation is running.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Root != null)
        {
            foreach (RpfNode node in EnumerateDescendants(Root))
            {
                node.PendingData?.Dispose();
                node.PendingData = null;
            }
        }

        if (!_leaveOpen)
        {
            _source.Dispose();
        }

        _ioLock.Dispose();
    }
}
