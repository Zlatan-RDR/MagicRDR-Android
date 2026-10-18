using System.Buffers.Binary;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class ResourceUnpackTests
{
    private static byte[] Payload(string text, int repeat = 40) =>
        System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(text, repeat)));

    private static async Task<byte[]> Frame(byte[] payload)
    {
        using var ms = new MemoryStream();
        await RpfCompression.ZstdCompressAsync(new MemoryStream(payload), ms);
        return ms.ToArray();
    }

    /// <summary>RSC05 header as written by the Switch (little-endian) followed by the given body.</summary>
    private static byte[] Resource(params byte[][] parts)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(RpfResourceInfo.Rsc05Magic));
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(BitConverter.GetBytes(Rsc05.Flags(256, 0)));
        foreach (byte[] part in parts)
        {
            bytes.AddRange(part);
        }

        return bytes.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task Unpack_IgnoresPaddingAfterTheFrame(int paddingBytes)
    {
        byte[] payload = Payload("hello wild west ");
        byte[] resource = Resource(await Frame(payload), new byte[paddingBytes]);

        Assert.Equal(payload, await RpfResourceUnpacker.UnpackAsync(resource, RpfPlatform.Switch));
    }

    [Fact]
    public async Task Unpack_JoinsConcatenatedFrames()
    {
        byte[] first = Payload("first ");
        byte[] second = Payload("second ");
        byte[] resource = Resource(await Frame(first), await Frame(second));

        Assert.Equal(first.Concat(second).ToArray(), await RpfResourceUnpacker.UnpackAsync(resource, RpfPlatform.Switch));
    }

    [Fact]
    public async Task Unpack_ReportsATruncatedFrameWithItsPosition()
    {
        byte[] frame = await Frame(Payload("truncate me "));
        byte[] resource = Resource(frame.AsSpan(0, frame.Length - 5).ToArray());

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => RpfResourceUnpacker.UnpackAsync(resource, RpfPlatform.Switch));

        Assert.Contains("cut off", error.Message);
        Assert.Contains("byte 12", error.Message);
    }

    [Fact]
    public async Task Unpack_RepairsAFrameThatHasNoEndMarker()
    {
        // what the Switch resources look like: blocks are complete, but the last one is not flagged "last"
        byte[] payload = Payload("open ended frame ", 30);
        var frame = new List<byte>();
        frame.AddRange(BitConverter.GetBytes(ZstdFrames.Magic));
        frame.Add(0x00);
        frame.Add(0x58);
        foreach (byte[] block in new[] { payload.AsSpan(0, 200).ToArray(), payload.AsSpan(200).ToArray() })
        {
            uint header = (uint)block.Length << 3; // raw block, not the last one
            frame.Add((byte)header);
            frame.Add((byte)(header >> 8));
            frame.Add((byte)(header >> 16));
            frame.AddRange(block);
        }

        RpfUnpackResult result = await RpfResourceUnpacker.UnpackDetailedAsync(Resource(frame.ToArray()), RpfPlatform.Switch);

        Assert.Equal(payload, result.Data);
        Assert.Equal(1, result.OpenEndedFrames);
        Assert.Contains("no end marker", RpfResourceUnpacker.Describe(Resource(frame.ToArray()), RpfPlatform.Switch));
    }

    [Fact]
    public async Task Unpack_RejectsDataSmallerThanTheFlagsDeclare()
    {
        byte[] payload = Payload("short ", 10); // 60 bytes
        var resource = new List<byte>();
        resource.AddRange(BitConverter.GetBytes(RpfResourceInfo.Rsc05Magic));
        resource.AddRange(BitConverter.GetBytes(1));
        resource.AddRange(BitConverter.GetBytes(Rsc05.Flags(8192, 0)));
        resource.AddRange(await Frame(payload));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => RpfResourceUnpacker.UnpackAsync(resource.ToArray(), RpfPlatform.Switch));

        Assert.Contains("declares 8,192", error.Message);
    }

    [Fact]
    public async Task Describe_SummarisesHeaderFramesAndPadding()
    {
        byte[] frame = await Frame(Payload("describe "));
        string text = RpfResourceUnpacker.Describe(Resource(frame, new byte[2]), RpfPlatform.Switch);

        Assert.StartsWith("RSC05 type 1 flags 0xC0000001 body ", text);
        Assert.Contains("frame 1: " + frame.Length + " B", text);
        Assert.EndsWith("2 trailing byte(s)", text);
        Assert.Equal(string.Empty, RpfResourceUnpacker.Describe(new byte[40], RpfPlatform.Switch)); // not a resource
    }
}

public class ZstdFramesTests
{
    // hand-made frame: header (single segment, 1-byte content size) + one raw block "abc" (last block)
    private static byte[] RawFrame()
    {
        var f = new List<byte>();
        f.AddRange(BitConverter.GetBytes(ZstdFrames.Magic));
        f.Add(0x20);                       // single segment, no checksum, no dictionary, size flag 0 -> 1 byte
        f.Add(3);                          // content size = 3
        uint blockHeader = 1u | (0u << 1) | (3u << 3);
        f.Add((byte)blockHeader);
        f.Add((byte)(blockHeader >> 8));
        f.Add((byte)(blockHeader >> 16));
        f.AddRange(new byte[] { (byte)'a', (byte)'b', (byte)'c' });
        return f.ToArray();
    }

    [Fact]
    public void TryReadFrame_MeasuresAFrameExactly_EvenWithPaddingAfterIt()
    {
        byte[] frame = RawFrame();
        byte[] padded = frame.Concat(new byte[] { 0, 0, 0 }).ToArray();

        Assert.True(ZstdFrames.TryReadFrame(padded, out ZstdFrame found, out string? error));
        Assert.Null(error);
        Assert.Equal(frame.Length, found.Length);
        Assert.Equal(3, found.ContentSize);
        Assert.False(found.HasChecksum);
    }

    [Fact]
    public void TryReadFrame_RejectsCutOffAndNonFrames()
    {
        byte[] frame = RawFrame();
        Assert.False(ZstdFrames.TryReadFrame(frame.AsSpan(0, frame.Length - 1), out _, out string? cut));
        Assert.Contains("needs 3 bytes", cut);

        Assert.False(ZstdFrames.TryReadFrame(new byte[] { 1, 2, 3, 4, 5 }, out _, out string? magic));
        Assert.Contains("magic", magic);
        Assert.False(ZstdFrames.IsFrameStart(new byte[] { 0, 0, 0, 0 }));
    }

    [Fact]
    public void TryReadFrame_FlagsAFrameWithoutEndMarkerAsOpenEnded_AndTerminateCompletesIt()
    {
        var frame = new List<byte>();
        frame.AddRange(BitConverter.GetBytes(ZstdFrames.Magic));
        frame.Add(0x04);   // checksum flag set, but the end (and checksum) were never written
        frame.Add(0x58);
        uint header = 3u << 3;
        frame.Add((byte)header);
        frame.Add((byte)(header >> 8));
        frame.Add((byte)(header >> 16));
        frame.AddRange(new byte[] { 1, 2, 3 });
        byte[] bytes = frame.ToArray();

        Assert.True(ZstdFrames.TryReadFrame(bytes, out ZstdFrame open, out _));
        Assert.True(open.IsOpenEnded);
        Assert.Equal(bytes.Length, open.Length);

        byte[] fixedBytes = ZstdFrames.Terminate(bytes, open);
        Assert.True(ZstdFrames.TryReadFrame(fixedBytes, out ZstdFrame closed, out _));
        Assert.False(closed.IsOpenEnded);
        Assert.False(closed.HasChecksum);
        Assert.Equal(bytes.Length + 3, closed.Length);
    }

    [Fact]
    public void TryReadFrame_SkipsSkippableFrames()
    {
        byte[] skippable = BitConverter.GetBytes(0x184D2A50u)
            .Concat(BitConverter.GetBytes(5u)).Concat(new byte[5]).ToArray();

        Assert.True(ZstdFrames.TryReadFrame(skippable, out ZstdFrame found, out _));
        Assert.True(found.IsSkippable);
        Assert.Equal(13, found.Length);
    }
}
