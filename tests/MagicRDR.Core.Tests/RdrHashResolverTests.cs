using System.Linq;
using System.Threading;
using MagicRDR.Core;
using Xunit;

namespace MagicRDR.Core.Tests;

public class RdrHashResolverTests
{
    // Small lists: lower the "common enough" thresholds so a handful of names is enough.
    private static readonly RdrResolveOptions Small = new() { MinExtensionCount = 1, MinTokenCount = 1 };

    private static RdrHashTarget File(string name) => new(RpfHash.GetHash(name), false, false, 0);

    private static RdrHashTarget Folder(string name) => new(RpfHash.GetHash(name), true, false, 0);

    private static RdrKnownEntry Known(string name, bool isResource = false, byte type = 0) => new(name, false, isResource, type);

    [Fact]
    public void FindsTheSameStemUnderAnotherExtension()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { File("alpha.xsc") },
            new[] { "alpha.wsc", "beta.xsc" },
            Array.Empty<RdrKnownEntry>(),
            Small);

        RdrResolvedName found = Assert.Single(result.Found);
        Assert.Equal("alpha.xsc", found.Name);
        Assert.Equal(0, result.Unresolved);
    }

    [Fact]
    public void FindsAFolderNameWithoutExtension()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { Folder("alpha") },
            new[] { "alpha.wsc" },
            Array.Empty<RdrKnownEntry>(),
            Small);

        Assert.Equal("alpha", Assert.Single(result.Found).Name);
    }

    [Fact]
    public void FindsTheMissingNumberInASeries()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { File("scene_02.csc") },
            new[] { "scene_01.csc", "scene_03.csc" },
            new[] { Known("scene_01.csc"), Known("scene_03.csc") },
            Small);

        Assert.Equal("scene_02.csc", Assert.Single(result.Found).Name);
    }

    [Fact]
    public void SwapsPrefixesBetweenKnownNames()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { File("streamed_intro.csc") },
            new[] { "cutscene_intro.csc", "streamed_other.csc" },
            new[] { Known("cutscene_intro.csc"), Known("streamed_other.csc") },
            Small);

        Assert.Equal("streamed_intro.csc", Assert.Single(result.Found).Name);
    }

    [Fact]
    public void ResourceOnlyTakesExtensionsOfItsOwnType()
    {
        RdrKnownEntry[] local = { Known("bar.wtd", isResource: true, type: 5) };
        string[] names = { "foo.xsc", "bar.wtd" };

        // foo.wtd fits a type-5 resource ...
        RdrResolveResult fits = RdrHashResolver.Resolve(
            new[] { new RdrHashTarget(RpfHash.GetHash("foo.wtd"), false, true, 5) }, names, local, Small);
        Assert.Equal("foo.wtd", Assert.Single(fits.Found).Name);

        // ... but foo.xsc has the right hash and the wrong kind of extension, so it is refused.
        RdrResolveResult refused = RdrHashResolver.Resolve(
            new[] { new RdrHashTarget(RpfHash.GetHash("foo.xsc"), false, true, 5) }, names, local, Small);
        Assert.Empty(refused.Found);
        Assert.Equal(1, refused.Unresolved);
    }

    [Fact]
    public void FolderNeverTakesANameWithAnExtension()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { Folder("alpha.wsc") },
            new[] { "alpha.wsc" },
            Array.Empty<RdrKnownEntry>(),
            Small);

        Assert.Empty(result.Found);
        Assert.Equal(1, result.Unresolved);
    }

    [Fact]
    public void FileNeverTakesANameWithoutAnExtension()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { File("alpha") },
            new[] { "alpha.wsc" },
            Array.Empty<RdrKnownEntry>(),
            Small);

        Assert.Empty(result.Found);
    }

    [Fact]
    public void UnrelatedHashStaysUnresolved()
    {
        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { new RdrHashTarget(0x12345678, false, false, 0) },
            new[] { "alpha.wsc", "beta.xsc" },
            Array.Empty<RdrKnownEntry>(),
            Small);

        Assert.Empty(result.Found);
        Assert.Equal(1, result.Unresolved);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void Cancelled_ReturnsWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        RdrResolveResult result = RdrHashResolver.Resolve(
            new[] { File("alpha.xsc") },
            new[] { "alpha.wsc" },
            Array.Empty<RdrKnownEntry>(),
            Small,
            null,
            cts.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(result.Found);
    }

    [Fact]
    public void ResolvedNamesHashBackToTheirTargets()
    {
        RdrHashTarget[] targets = { File("alpha.xsc"), Folder("alpha") };

        RdrResolveResult result = RdrHashResolver.Resolve(
            targets,
            new[] { "alpha.wsc", "beta.xsc" },
            Array.Empty<RdrKnownEntry>(),
            Small);

        Assert.Equal(2, result.Found.Count);
        Assert.All(result.Found, found => Assert.Equal(found.Hash, RpfHash.GetHash(found.Name)));
    }
}
