using MagicRDR.Core;
using Xunit;

namespace MagicRDR.Core.Tests;

public class BatchMatcherTests
{
    private static async Task<Rpf6Archive> OpenAsync(string[] allNames, params string[] knownNames)
    {
        byte[] data = SyntheticRpf.Build(allNames.Select(n => new SyntheticRpf.Item(n, new byte[] { 1, 2, 3 }, Compressed: false)).ToArray());
        var names = new RpfNameDictionary();
        foreach (string name in knownNames)
        {
            names.Add(name);
        }

        return await Rpf6Archive.OpenAsync(new MemoryStream(data), new RpfOpenOptions { Names = names });
    }

    private static List<RpfNode> Files(Rpf6Archive archive) => archive.Root.Children.Where(c => !c.IsDirectory).ToList();

    [Fact]
    public async Task MatchesByExactName_IgnoringCase()
    {
        using Rpf6Archive archive = await OpenAsync(new[] { "strings.wst", "blob.bin" }, "strings.wst", "blob.bin");

        BatchMatchResult result = BatchMatcher.Match(Files(archive), new[] { "STRINGS.WST", "blob.bin" });

        Assert.Equal(2, result.Matches.Count);
        Assert.Empty(result.UnmatchedTargets);
        Assert.Empty(result.UnmatchedSources);
        Assert.Empty(result.AmbiguousTargets);
    }

    [Fact]
    public async Task MatchesWithoutExtension_InEitherDirection()
    {
        using Rpf6Archive archive = await OpenAsync(new[] { "strings.wst", "blob.bin" }, "strings.wst", "blob.bin");

        BatchMatchResult result = BatchMatcher.Match(Files(archive), new[] { "strings.txt", "blob" });

        Assert.Equal(2, result.Matches.Count);
        RpfNode strings = archive.Root.FindChild("strings.wst")!;
        Assert.Equal(0, result.Matches.Single(m => m.Node == strings).SourceIndex);
    }

    [Fact]
    public async Task MatchesByHexHashName_WithOrWithoutExtension_AndWithoutLeadingZeros()
    {
        // Nothing is named, so every entry is known only by its hash.
        using Rpf6Archive archive = await OpenAsync(new[] { "alpha.bin", "beta.bin", "gamma.bin" });
        List<RpfNode> files = Files(archive);
        uint a = files[0].NameHash;
        uint b = files[1].NameHash;
        uint c = files[2].NameHash;

        BatchMatchResult result = BatchMatcher.Match(files, new[]
        {
            RpfHash.FormatHexHash(a),                 // 0x00FEB5CC
            RpfHash.FormatHexHash(b) + ".bin",        // with extension
            "0x" + c.ToString("x"),                   // lower case, no leading zeros
        });

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(0, result.Matches.Single(m => m.Node == files[0]).SourceIndex);
        Assert.Equal(1, result.Matches.Single(m => m.Node == files[1]).SourceIndex);
        Assert.Equal(2, result.Matches.Single(m => m.Node == files[2]).SourceIndex);
    }

    [Fact]
    public async Task ReportsUnmatchedEntriesAndFiles()
    {
        using Rpf6Archive archive = await OpenAsync(new[] { "one.bin", "two.bin" }, "one.bin", "two.bin");

        BatchMatchResult result = BatchMatcher.Match(Files(archive), new[] { "one.bin", "stranger.bin" });

        Assert.Single(result.Matches);
        Assert.Equal("two.bin", Assert.Single(result.UnmatchedTargets).Name);
        Assert.Equal(1, Assert.Single(result.UnmatchedSources));
    }

    [Fact]
    public async Task SkipsEntriesThatTwoFilesFit()
    {
        using Rpf6Archive archive = await OpenAsync(new[] { "a.wst" }, "a.wst");

        BatchMatchResult result = BatchMatcher.Match(Files(archive), new[] { "a.txt", "a.xml" });

        Assert.Empty(result.Matches);
        Assert.Single(result.AmbiguousTargets);
    }

    [Fact]
    public async Task SkipsFilesThatTwoEntriesWant()
    {
        using Rpf6Archive archive = await OpenAsync(new[] { "a.wst", "a.wsc" }, "a.wst", "a.wsc");

        BatchMatchResult result = BatchMatcher.Match(Files(archive), new[] { "a.txt" });

        Assert.Empty(result.Matches);
        Assert.Equal(2, result.AmbiguousTargets.Count);
        Assert.Equal(0, Assert.Single(result.UnmatchedSources));
    }

    [Fact]
    public async Task IgnoresFolderPartsInFileNames()
    {
        using Rpf6Archive archive = await OpenAsync(new[] { "one.bin" }, "one.bin");

        BatchMatchResult result = BatchMatcher.Match(Files(archive), new[] { "translated/one.bin" });

        Assert.Single(result.Matches);
    }

    [Fact]
    public void UniqueName_AddsASuffixBeforeTheExtension()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("a.bin", BatchNaming.UniqueName("a.bin", used));
        Assert.Equal("A_2.BIN", BatchNaming.UniqueName("A.BIN", used));
        Assert.Equal("a_3.bin", BatchNaming.UniqueName("a.bin", used));
        Assert.Equal("0x00FEB5CC", BatchNaming.UniqueName("0x00FEB5CC", used));
        Assert.Equal("0x00FEB5CC_2", BatchNaming.UniqueName("0x00FEB5CC", used));
    }
}
