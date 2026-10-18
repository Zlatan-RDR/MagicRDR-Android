using System.Buffers.Binary;
using System.Text;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class StringTableTests
{
    private const uint Tag = 0x50000000;

    /// <summary>A tiny hand-made table with one bucket holding two chained entries: 0xAAAA0001=Hello, 0xBBBB0002=World.</summary>
    private static byte[] BuildTable()
    {
        byte[] d = new byte[0xA0];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), value);

        U32(0x00, 0x12345678);           // VMT
        U32(0x08, Tag | 0x20);           // -> info block
        U32(0x20, 2);                    // bucket count
        U32(0x24, Tag | 0x30);           // -> bucket table
        U32(0x30, Tag | 0x40);           // bucket 0 -> head node
        U32(0x34, 0);                    // bucket 1 empty
        U32(0x44, Tag | 0x60);           // head: first entry
        U32(0x48, Tag | 0x50);           // head: next chain node
        U32(0x54, Tag | 0x70);           // chain node: second entry
        U32(0x58, 0);                    // end of chain
        U32(0x60, 0xAAAA0001);           // entry 1 key
        U32(0x64, Tag | 0x80);           // entry 1 text
        U32(0x70, 0xBBBB0002);           // entry 2 key
        U32(0x74, Tag | 0x90);           // entry 2 text
        Encoding.UTF8.GetBytes("Hello\0").CopyTo(d, 0x80);
        Encoding.UTF8.GetBytes("World\0").CopyTo(d, 0x90);
        return d;
    }

    [Fact]
    public void Read_ReturnsEntriesInChainOrder()
    {
        var entries = StringTableReader.Read(BuildTable(), littleEndian: true);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new StringTableEntry(0xAAAA0001, 0, "Hello"), entries[0]);
        Assert.Equal(new StringTableEntry(0xBBBB0002, 1, "World"), entries[1]);
    }

    [Fact]
    public void Read_RejectsTruncatedData()
    {
        byte[] cut = BuildTable().AsSpan(0, 0x68).ToArray();
        Assert.Throws<InvalidDataException>(() => StringTableReader.Read(cut, littleEndian: true));
    }

    [Fact]
    public async Task SwitchResource_UnpacksThenReads()
    {
        byte[] payload = BuildTable();
        Array.Resize(ref payload, 256); // resource sizes are multiples of 256
        using var compressed = new MemoryStream();
        await RpfCompression.ZstdCompressAsync(new MemoryStream(payload), compressed);

        // RSC05 header, little-endian (Switch): magic, resource type 1 (string table), flags with the resource bit
        var resource = new List<byte>();
        resource.AddRange(BitConverter.GetBytes(RpfResourceInfo.Rsc05Magic));
        resource.AddRange(BitConverter.GetBytes(1));
        resource.AddRange(BitConverter.GetBytes(Rsc05.Flags(256, 0)));
        resource.AddRange(compressed.ToArray());

        byte[] unpacked = await RpfResourceUnpacker.UnpackAsync(resource.ToArray(), RpfPlatform.Switch);

        Assert.Equal(payload, unpacked);
        Assert.Equal("Hello", StringTableReader.Read(unpacked, true)[0].Value);
    }

    [Fact]
    public async Task Unpack_RejectsNonSwitchAndNonResources()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => RpfResourceUnpacker.UnpackAsync(new byte[32], RpfPlatform.Xbox));
        await Assert.ThrowsAsync<InvalidDataException>(() => RpfResourceUnpacker.UnpackAsync(new byte[32], RpfPlatform.Switch));
    }
}
