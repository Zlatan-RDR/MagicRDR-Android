using System.Text;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class EmbeddedNamesTests
{
    private static byte[] NamesTable(int entryCount, params string[] names)
    {
        // entryCount * 8 bytes of (name offset, time) pairs, then the NUL-separated names, padded to the AES block size
        var bytes = new List<byte>(new byte[entryCount * 8]);
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(name));
            bytes.Add(0);
        }

        while (bytes.Count % 16 != 0)
        {
            bytes.Add(0);
        }

        return bytes.ToArray();
    }

    private static MemoryStream Archive(byte[] table, out int offsetInEightByteUnits)
    {
        var bytes = new byte[2048].Concat(table).ToArray();
        offsetInEightByteUnits = 2048 / 8;
        return new MemoryStream(bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_FindsNamesWhetherTheTableIsEncryptedOrNot(bool encrypted)
    {
        byte[] table = NamesTable(3, "subtitle_a-en.wst", "not_in_archive.txt", "strings.strtbl");
        if (encrypted)
        {
            table = RpfCrypto.Encrypt(table);
        }

        using MemoryStream stream = Archive(table, out int offset);
        var unresolved = new HashSet<uint> { RpfHash.GetHash("subtitle_a-en.wst"), RpfHash.GetHash("strings.strtbl"), 0x12345678u };
        var names = new RpfNameDictionary();

        int added = await RpfEmbeddedNames.LoadAsync(stream, offset, 3, unresolved, names, RpfCrypto.DefaultKey);

        Assert.Equal(2, added);
        Assert.Equal("subtitle_a-en.wst", names.Resolve(RpfHash.GetHash("subtitle_a-en.wst")));
        Assert.False(names.Contains(RpfHash.GetHash("not_in_archive.txt"))); // no entry has this hash: never accepted
    }

    [Fact]
    public async Task LoadAsync_DoesNothingWithoutADebugTable()
    {
        using var stream = new MemoryStream(new byte[4096]);
        int added = await RpfEmbeddedNames.LoadAsync(stream, 0, 3, new HashSet<uint> { 1 }, new RpfNameDictionary(), RpfCrypto.DefaultKey);

        Assert.Equal(0, added);
    }

    [Fact]
    public async Task OpeningAnArchive_ResolvesNamesStoredInsideIt()
    {
        byte[] archive = SyntheticRpf.Build(
            new SyntheticRpf.Item("alpha.xml", Encoding.ASCII.GetBytes("<a/>"), Compressed: false),
            new SyntheticRpf.Item("beta.txt", Encoding.ASCII.GetBytes("beta"), Compressed: false));

        // append the names table after the data and point the header's debug offset at it
        int padding = (8 - archive.Length % 8) % 8;
        byte[] withTable = archive.Concat(new byte[padding]).ToArray();
        int offsetUnits = withTable.Length / 8;
        withTable = withTable.Concat(NamesTable(3, "alpha.xml", "beta.txt")).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(withTable.AsSpan(8), offsetUnits);

        using Rpf6Archive opened = await Rpf6Archive.OpenAsync(new MemoryStream(withTable));

        Assert.Equal(2, opened.EmbeddedNamesFound);
        Assert.Equal("alpha.xml", opened.Root.FindChild(RpfHash.GetHash("alpha.xml"))!.Name);
        Assert.Equal("beta.txt", opened.Root.FindChild(RpfHash.GetHash("beta.txt"))!.Name);
    }
}
