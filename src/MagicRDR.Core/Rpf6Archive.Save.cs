using System.Buffers.Binary;
using System.IO.Compression;

namespace MagicRDR.Core;

public sealed partial class Rpf6Archive
{
    // The original tool pads the header + TOC region with zeros up to this position before the first file
    // (it writes 671743 bytes, then every entry is aligned up, so data effectively starts at 0xA4000).
    private const long MinDataPosition = 671743;

    private const long LargeFileThreshold = 131072;
    private const long TempInMemoryLimit = 16L * 1024 * 1024;

    /// <summary>
    /// Writes a complete new archive to <paramref name="destination"/>: header, rebuilt table of contents and file data.
    /// Unchanged files are copied verbatim (compressed bytes are not re-encoded); replaced/added plain files are compressed
    /// on the fly. The in-memory model is <b>not</b> modified, so the same instance can be saved again — but to continue
    /// editing the saved result, open it as a new <see cref="Rpf6Archive"/> (offsets in this instance still refer to the source).
    /// <para>The destination must be seekable (the TOC is written last, after the final offsets are known) and must not be the source stream/file.</para>
    /// </summary>
    public async Task SaveAsync(
        Stream destination,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException("The destination stream must be writable and seekable.", nameof(destination));
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(destination, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Saves to a local file; a partially written file is deleted if saving fails or is cancelled.</summary>
    public async Task SaveToFileAsync(
        string path,
        IProgress<RpfProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        bool success = false;
        try
        {
            await SaveAsync(stream, progress, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            success = true;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (!success)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task SaveCoreAsync(Stream output, IProgress<RpfProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new RpfProgress(0, "Rebuilding table of contents"));

        // ---- 1. layout (ported from RPF6TOC.Rebuild): every directory's children are contiguous and sorted by name hash.
        var ordered = new List<RpfNode> { Root };
        var firstChildIndex = new Dictionary<RpfNode, int>();
        LayoutChildren(Root, ordered, firstChildIndex);

        int entryCount = ordered.Count;
        int tocSize = RpfHeader.ComputeTocSize(entryCount);

        // ---- 2. decide the order in which file data is written (ported from RPF6.Write):
        //         existing/replaced files keep their relative archive order, new files go last (resources first).
        List<RpfNode> files = ordered.Where(n => !n.IsDirectory).ToList();
        static long? OrderKey(RpfNode n) =>
            n.AsFile!.KeepOffset ?? (n.ExistedInArchive && n.PendingData == null ? (long?)n.AsFile.GetOffset() : null);

        var writeOrder = new List<RpfNode>(files.Count);
        writeOrder.AddRange(files.Where(n => OrderKey(n) != null).OrderBy(n => OrderKey(n)!.Value));
        List<RpfNode> fresh = files.Where(n => OrderKey(n) == null).ToList();
        writeOrder.AddRange(fresh.Where(n => n.IsResource));
        writeOrder.AddRange(fresh.Where(n => !n.IsResource));

        long totalWork = 0;
        foreach (RpfNode n in writeOrder)
        {
            totalWork += n.PendingData?.Length ?? n.AsFile!.SizeInArchive;
        }

        var tracker = new ProgressTracker(progress, totalWork);

        // ---- 3. header + zeroed TOC placeholder (the real TOC is written last)
        output.Position = 0;
        output.SetLength(0);

        byte[] header = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), entryCount);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), Header.DebugDataOffset);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), Header.EncryptionFlag);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await StreamUtil.WriteZerosAsync(output, tocSize, ct).ConfigureAwait(false);

        long position = output.Position;
        await StreamUtil.WriteZerosAsync(output, StreamUtil.RoundUp(position, 2048) - position, ct).ConfigureAwait(false);
        position = output.Position;
        if (position < MinDataPosition)
        {
            await StreamUtil.WriteZerosAsync(output, MinDataPosition - position, ct).ConfigureAwait(false);
        }

        // ---- 4. file data
        var placed = new Dictionary<RpfNode, (long Offset, int Size)>(writeOrder.Count);
        int index = 0;
        foreach (RpfNode node in writeOrder)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            tracker.SetMessage($"Writing {node.Name} ({index}/{writeOrder.Count})");

            RpfFileEntry file = node.AsFile!;
            Stream? pending = node.PendingData;
            Stream? compressed = null;
            try
            {
                bool compress = pending != null && file.Flags.IsCompressed && !file.Flags.IsResource;
                long size;
                if (compress)
                {
                    // Compressed size must be known before alignment is chosen, so compress first (memory or temp file).
                    pending!.Position = 0;
                    compressed = CreateTempStream(pending.Length);
                    if (Platform == RpfPlatform.Switch)
                    {
                        await RpfCompression.ZstdCompressAsync(pending, compressed, RpfCompression.DefaultZstdLevel, tracker.Advance, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RpfCompression.DeflateAsync(pending, compressed, zlibHeader: false, CompressionLevel.SmallestSize, tracker.Advance, ct).ConfigureAwait(false);
                    }

                    pending.Position = 0;
                    compressed.Position = 0;
                    size = compressed.Length;
                }
                else
                {
                    size = pending?.Length ?? file.SizeInArchive;
                }

                if (size > MaxStoredSize)
                {
                    throw new InvalidDataException($"'{node.Name}' is {size:N0} bytes after compression; RPF6 entries are limited to 256 MiB.");
                }

                // Alignment rules copied from the original: the gap is never zero (an aligned position gets a full extra unit).
                long unit = file.Flags.IsResource ? 2048 : (size >= LargeFileThreshold ? 2048 : 8);
                long current = output.Position;
                long gap = StreamUtil.RoundUp(current, unit) - current;
                if (gap == 0)
                {
                    gap = unit;
                }

                await StreamUtil.WriteZerosAsync(output, gap, ct).ConfigureAwait(false);
                long offset = output.Position;

                if (compressed != null)
                {
                    await StreamUtil.CopyExactAsync(compressed, output, size, null, ct).ConfigureAwait(false);
                }
                else if (pending != null)
                {
                    pending.Position = 0;
                    await StreamUtil.CopyExactAsync(pending, output, size, tracker.Advance, ct).ConfigureAwait(false);
                    pending.Position = 0;
                }
                else
                {
                    long sourceOffset = file.GetOffset();
                    if (sourceOffset < 0 || sourceOffset + size > _source.Length)
                    {
                        throw new InvalidDataException($"'{node.Name}' points outside the source archive.");
                    }

                    _source.Position = sourceOffset;
                    await StreamUtil.CopyExactAsync(_source, output, size, tracker.Advance, ct).ConfigureAwait(false);
                }

                placed[node] = (offset, (int)size);
            }
            finally
            {
                if (compressed != null)
                {
                    await compressed.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        long end = output.Position;
        await StreamUtil.WriteZerosAsync(output, StreamUtil.RoundUp(end, 2048) - end, ct).ConfigureAwait(false);

        // ---- 5. real TOC
        tracker.SetMessage("Writing table of contents");
        byte[] toc = BuildToc(ordered, firstChildIndex, placed, tocSize);
        if (Header.IsEncrypted)
        {
            toc = await Task.Run(() => RpfCrypto.Encrypt(toc, _encryptionKey), ct).ConfigureAwait(false);
        }

        output.Position = 16;
        await output.WriteAsync(toc, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        tracker.Complete("Archive saved");
    }

    private static void LayoutChildren(RpfNode directory, List<RpfNode> ordered, Dictionary<RpfNode, int> firstChildIndex)
    {
        if (directory.Children.Count == 0)
        {
            return;
        }

        firstChildIndex[directory] = ordered.Count;
        ordered.AddRange(directory.Children.OrderBy(c => c.NameHash)); // stable sort, like the original

        foreach (RpfNode child in directory.Children)
        {
            if (child.IsDirectory)
            {
                LayoutChildren(child, ordered, firstChildIndex);
            }
        }
    }

    private static byte[] BuildToc(
        List<RpfNode> ordered,
        Dictionary<RpfNode, int> firstChildIndex,
        Dictionary<RpfNode, (long Offset, int Size)> placed,
        int tocSize)
    {
        byte[] toc = new byte[tocSize];
        for (int i = 0; i < ordered.Count; i++)
        {
            RpfNode node = ordered[i];
            Span<byte> e = toc.AsSpan(i * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt32BigEndian(e.Slice(0, 4), node.NameHash);

            if (node.Entry is RpfDirectoryEntry dir)
            {
                int first = firstChildIndex.TryGetValue(node, out int idx) ? idx : 0;
                BinaryPrimitives.WriteInt32BigEndian(e.Slice(4, 4), dir.Flags);
                BinaryPrimitives.WriteUInt32BigEndian(e.Slice(8, 4), 0x80000000u | (uint)(first & int.MaxValue));
                BinaryPrimitives.WriteInt32BigEndian(e.Slice(12, 4), node.Children.Count & 0x0FFFFFFF);
                BinaryPrimitives.WriteInt32BigEndian(e.Slice(16, 4), dir.Unknown);
            }
            else
            {
                RpfFileEntry file = node.AsFile!;
                (long offset, int size) = placed[node];
                uint raw = RpfFileEntry.EncodeOffset(offset, file.Flags.IsResource, file.ResourceType);

                BinaryPrimitives.WriteInt32BigEndian(e.Slice(4, 4), size);
                BinaryPrimitives.WriteUInt32BigEndian(e.Slice(8, 4), raw);
                BinaryPrimitives.WriteInt32BigEndian(e.Slice(12, 4), file.Flags.Flag1);
                BinaryPrimitives.WriteInt32BigEndian(e.Slice(16, 4), file.Flags.Flag2);
            }
        }

        return toc;
    }

    private Stream CreateTempStream(long expectedSize)
    {
        if (expectedSize <= TempInMemoryLimit)
        {
            return new MemoryStream();
        }

        string path = Path.Combine(TempDirectory, $"rpf_{Guid.NewGuid():N}.tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
