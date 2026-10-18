using System.Text;
using MagicRDR.Core;
using Xunit;

namespace MagicRDR.Core.Tests;

public class Rpf6ArchiveTests
{
    private static readonly byte[] TextV1 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello wild west\n", 400)));
    private static readonly byte[] Blob = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();

    private static RpfNameDictionary Names()
    {
        var names = new RpfNameDictionary();
        names.Add("strings.txt");
        names.Add("blob.bin");
        return names;
    }

    private static byte[] Sample() => SyntheticRpf.Build(
        new SyntheticRpf.Item("strings.txt", TextV1, Compressed: true),
        new SyntheticRpf.Item("blob.bin", Blob, Compressed: false));

    private static async Task<byte[]> ExtractAsync(Rpf6Archive archive, string name)
    {
        RpfNode node = archive.Root.FindChild(name) ?? throw new InvalidOperationException(name);
        using var ms = new MemoryStream();
        await archive.ExtractFileAsync(node, ms);
        return ms.ToArray();
    }

    [Fact]
    public void Hash_MatchesJenkinsOneAtATimeVectors()
    {
        Assert.Equal(0xCA2E9442u, RpfHash.GetHash("a"));
        Assert.Equal(0x3F75CCC1u, RpfHash.GetHash("test"));
        Assert.Equal(RpfHash.GetHash("TEST"), RpfHash.GetHash("test")); // case-insensitive
    }

    [Fact]
    public async Task Open_ParsesTreeAndResolvesNames()
    {
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });

        Assert.Equal(RpfPlatform.Xbox, archive.Platform);
        Assert.Equal(2, archive.Root.Children.Count);
        Assert.NotNull(archive.FindByPath("root/strings.txt"));
        Assert.True(archive.FindByPath("root/strings.txt")!.IsCompressed);
        Assert.False(archive.FindByPath("root/blob.bin")!.IsCompressed);
    }

    [Fact]
    public async Task Open_RejectsNonRpf()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Rpf6Archive.OpenAsync(new MemoryStream(new byte[64])));
    }

    [Fact]
    public async Task Extract_InflatesCompressedAndCopiesStored()
    {
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });

        Assert.Equal(TextV1, await ExtractAsync(archive, "strings.txt"));
        Assert.Equal(Blob, await ExtractAsync(archive, "blob.bin"));
    }

    [Fact]
    public async Task ReplaceThenSave_RoundTripsAndLeavesOtherFilesAlone()
    {
        byte[] translated = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hola lejano oeste\n", 700)));

        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });
        archive.ReplaceFile(archive.Root.FindChild("strings.txt")!, new MemoryStream(translated));
        Assert.True(archive.HasPendingChanges);

        var saved = new MemoryStream();
        var seen = new List<double>();
        await archive.SaveAsync(saved, new Progress<RpfProgress>(p => seen.Add(p.Fraction)));

        using Rpf6Archive reopened = await Rpf6Archive.OpenAsync(new MemoryStream(saved.ToArray()), new RpfOpenOptions { Names = Names() });
        Assert.Equal(translated, await ExtractAsync(reopened, "strings.txt"));
        Assert.Equal(Blob, await ExtractAsync(reopened, "blob.bin"));
        Assert.True(reopened.Root.FindChild("strings.txt")!.IsCompressed);   // stayed compressed
        Assert.True(reopened.Root.FindChild("strings.txt")!.AsFile!.SizeInArchive < translated.Length);

        foreach (RpfNode file in reopened.Root.Children)
        {
            Assert.Equal(0, file.AsFile!.GetOffset() % 8);
        }
    }

    [Fact]
    public async Task Save_WithoutChanges_PreservesAllContent()
    {
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });

        var saved = new MemoryStream();
        await archive.SaveAsync(saved);

        using Rpf6Archive reopened = await Rpf6Archive.OpenAsync(new MemoryStream(saved.ToArray()), new RpfOpenOptions { Names = Names() });
        Assert.Equal(TextV1, await ExtractAsync(reopened, "strings.txt"));
        Assert.Equal(Blob, await ExtractAsync(reopened, "blob.bin"));
    }

    [Fact]
    public async Task AddAndRemove_AreReflectedAfterSave()
    {
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });

        byte[] extra = Encoding.UTF8.GetBytes("<cfg/>");
        archive.AddFile(archive.Root, "extra.xml", new MemoryStream(extra));
        archive.RemoveNode(archive.Root.FindChild("blob.bin")!);

        var saved = new MemoryStream();
        await archive.SaveAsync(saved);

        using Rpf6Archive reopened = await Rpf6Archive.OpenAsync(new MemoryStream(saved.ToArray()), new RpfOpenOptions { Names = Names() });
        Assert.Null(reopened.Root.FindChild("blob.bin"));
        Assert.Equal(extra, await ExtractAsync(reopened, "extra.xml"));
        Assert.True(reopened.Root.FindChild("extra.xml")!.IsCompressed); // .xml is a text format => compressed
    }

    [Fact]
    public async Task Replace_PlainFileWithResourceHeader_IsRejected()
    {
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });

        var fakeResource = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fakeResource, RpfResourceInfo.Rsc05Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(fakeResource.AsSpan(8), unchecked((int)0xC0000010));

        using var content = new MemoryStream(fakeResource);
        Assert.Throws<InvalidDataException>(() => archive.ReplaceFile(archive.Root.FindChild("blob.bin")!, content));
    }

    [Fact]
    public void Hash_IncrementalHelpersMatchGetHash()
    {
        uint state = RpfHash.Accumulate(0, "strings".AsSpan());
        Assert.Equal(RpfHash.GetHash("strings.txt"), RpfHash.Finish(RpfHash.Accumulate(state, ".txt".AsSpan())));
        Assert.Equal(RpfHash.GetHash("Test"), RpfHash.GetHash("test".AsSpan()));
    }

    [Fact]
    public async Task NameDictionary_SkipsCommentsAndBlankLines()
    {
        var names = new RpfNameDictionary();
        using var text = new MemoryStream(Encoding.UTF8.GetBytes("# comment\n\nmapres.rpf\n  blob.bin  \n"));
        int added = await names.LoadAsync(text);

        Assert.Equal(2, added);
        Assert.Equal("blob.bin", names.Resolve(RpfHash.GetHash("blob.bin")));
        Assert.False(names.Contains(RpfHash.GetHash("# comment")));
    }

    [Fact]
    public async Task Harvest_FindsNamesMentionedInTextFiles()
    {
        // only notes.txt is named; the other two entries show up as hashes until the harvester finds them
        byte[] notes = Encoding.UTF8.GetBytes("see strings and blob.bin for details");
        byte[] bytes = SyntheticRpf.Build(
            new SyntheticRpf.Item("notes.txt", notes, Compressed: false),
            new SyntheticRpf.Item("strings.txt", TextV1, Compressed: true),
            new SyntheticRpf.Item("blob.bin", Blob, Compressed: false));

        var names = new RpfNameDictionary();
        names.Add("notes.txt");
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(bytes), new RpfOpenOptions { Names = names });
        Assert.StartsWith("0x", archive.Root.FindChild(RpfHash.GetHash("blob.bin"))!.Name); // unnamed before the scan

        RpfHarvestResult result = await RpfNameHarvester.HarvestAsync(archive);

        Assert.Equal(2, result.Found);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal("blob.bin", archive.Root.FindChild(RpfHash.GetHash("blob.bin"))!.Name);
        Assert.Equal("strings.txt", archive.Root.FindChild(RpfHash.GetHash("strings.txt"))!.Name);
    }

    [Fact]
    public async Task Cancel_DuringSave_Throws()
    {
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(Sample()), new RpfOpenOptions { Names = Names() });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.SaveAsync(new MemoryStream(), null, cts.Token));
    }
}
