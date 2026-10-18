using System.Text;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class TableWriterTests
{
    // Reference bytes produced by an independent, literal transcription of the original tool's StringTable.Write
    // (4 entries, 5 buckets, VMT 0x12345678, little-endian, no padding).
    private const string Golden =
        "7856341220000050300200500000000000000000cdcdcdcdcdcdcdcdcdcdcdcd00000000cdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" +
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd05000000400200500500050004000000" +
        "540200506c0200500000000000000000000000000a0000008402005060020050050000008c020050000000000100aaaa" +
        "94020050780200500200bbbb9c020050000000000a000000b002005005000000b40200500100aaaaa40200500200bbbb" +
        "aa02005048656c6c6f00576f726c640054656e004669766500";

    private static StringTableData Sample()
    {
        var table = new StringTableData { Vmt = 0x12345678, BucketCount = 5 };
        table.Entries.Add(new StringTableEntry(0xAAAA0001, 0, "Hello"));
        table.Entries.Add(new StringTableEntry(0xBBBB0002, 0, "World"));
        table.Entries.Add(new StringTableEntry(0x0000000A, 0, "Ten"));
        table.Entries.Add(new StringTableEntry(0x00000005, 0, "Five"));
        return table;
    }

    [Fact]
    public void Write_ProducesTheSameBytesAsTheOriginalLayout()
    {
        byte[] written = StringTableWriter.Write(Sample(), littleEndian: true, pageSize: 0);

        Assert.Equal(Golden, Convert.ToHexString(written).ToLowerInvariant());
    }

    [Fact]
    public void Write_ThenRead_KeepsKeysTextsVmtAndBucketCount()
    {
        StringTableData original = Sample();

        StringTableData back = StringTableReader.ReadDocument(StringTableWriter.Write(original, true, pageSize: 0), littleEndian: true);

        Assert.Equal(0x12345678u, back.Vmt);
        Assert.Equal(5u, back.BucketCount);
        Assert.Equal(
            original.Entries.Select(e => (e.Key, e.Value)).OrderBy(e => e.Key),
            back.Entries.Select(e => (e.Key, e.Value)).OrderBy(e => e.Key));
    }

    [Fact]
    public void Write_StoresNonAsciiTextAsUtf8_AndToleratesNullTextAndDuplicateKeys()
    {
        var table = new StringTableData { BucketCount = 7 };
        table.Entries.Add(new StringTableEntry(1, 0, "\u0645\u0631\u062D\u0628\u0627 caf\u00E9"));
        table.Entries.Add(new StringTableEntry(1, 0, "same key twice"));
        table.Entries.Add(new StringTableEntry(2, 0, null!));

        StringTableData back = StringTableReader.ReadDocument(StringTableWriter.Write(table, true, pageSize: 0), true);

        Assert.Equal(3, back.Entries.Count);
        Assert.Contains(back.Entries, e => e.Value == "\u0645\u0631\u062D\u0628\u0627 caf\u00E9");
        Assert.Contains(back.Entries, e => e.Value == "same key twice");
        Assert.Contains(back.Entries, e => e.Key == 2 && e.Value == string.Empty);
    }

    [Fact]
    public void Write_PadsToAPageSizeThatTheFlagsCanDescribe()
    {
        byte[] padded = StringTableWriter.Write(Sample(), true, StringTableWriter.DefaultPageSize);

        Assert.Equal(0, padded.Length % StringTableWriter.DefaultPageSize);
        Assert.True(Rsc05.IsRepresentable(padded.Length));
        Assert.Equal(0xCD, padded[^1]);
    }

    [Fact]
    public async Task Pack_ThenUnpackAndRead_RoundTripsThroughTheResourceFormat()
    {
        byte[] resource = await StringTableWriter.PackAsync(Sample(), RpfPlatform.Switch);

        Assert.Equal(RpfResourceInfo.Rsc05Magic, BitConverter.ToUInt32(resource, 0));
        Assert.Equal(1, BitConverter.ToInt32(resource, 4));
        Assert.Equal(Rsc05.Flags(StringTableWriter.DefaultPageSize, 0), BitConverter.ToUInt32(resource, 8));

        byte[] raw = await RpfResourceUnpacker.UnpackAsync(resource, RpfPlatform.Switch);
        StringTableData back = StringTableReader.ReadDocument(raw, littleEndian: true);

        Assert.Equal(4, back.Entries.Count);
        Assert.Equal("Hello", back.Entries.Single(e => e.Key == 0xAAAA0001).Value);
    }

    [Fact]
    public async Task Pack_RejectsPlatformsItCannotWrite()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => StringTableWriter.PackAsync(Sample(), RpfPlatform.Xbox));
    }

    [Theory]
    [InlineData(8192, 0x810)]
    [InlineData(65536, 0x2010)]   // seen on the device: flags 0xC0002010
    [InlineData(256, 0x1)]
    public void Rsc05_CompactSizeMatchesTheValuesSeenInRealArchives(int size, int compact)
    {
        Assert.Equal(compact, Rsc05.CompactSize(size));
        Assert.Equal(size, Rsc05.DecodeSize(compact));
        Assert.Equal(0xC0000000u | (uint)compact, Rsc05.Flags(size, 0));
    }
}
