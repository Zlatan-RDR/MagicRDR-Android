using System.Buffers;

namespace MagicRDR.Core;

/// <summary>Wraps a stream and swallows Dispose, so compression streams cannot close the real target.</summary>
internal sealed class LeaveOpenStream : Stream
{
    private readonly Stream _inner;

    public LeaveOpenStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);
}

/// <summary>
/// A forward-only, read-only window over <c>length</c> bytes of another stream, starting at its current position.
/// Lets decompressors stream straight out of the RPF without loading the compressed bytes into memory.
/// Does not own (or dispose) the inner stream.
/// </summary>
internal sealed class RangeReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _read;

    public RangeReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public long BytesRead => _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _read;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer.Slice(0, (int)remaining);
        }

        int n = _inner.Read(buffer);
        _read += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long remaining = _length - _read;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer.Slice(0, (int)remaining);
        }

        int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _read += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class StreamUtil
{
    public const int CopyBufferSize = 256 * 1024;

    private static readonly byte[] Zeros = new byte[64 * 1024];

    public static long RoundUp(long value, long multiple) => (value + multiple - 1) / multiple * multiple;

    public static async Task WriteZerosAsync(Stream destination, long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, Zeros.Length);
            await destination.WriteAsync(Zeros.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            count -= chunk;
        }
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes; throws if the source ends early.</summary>
    public static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long count,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        int bufferSize = (int)Math.Min((long)CopyBufferSize, count);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min((long)buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The archive ended before all of the file's data could be read.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                onBytes?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Copies until the source reports end of stream. Returns the number of bytes copied.</summary>
    public static async Task<long> CopyToEndAsync(
        Stream source,
        Stream destination,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return total;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                onBytes?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
