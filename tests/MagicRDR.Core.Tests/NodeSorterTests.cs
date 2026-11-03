using System.Linq;
using MagicRDR.Core;
using Xunit;

namespace MagicRDR.Core.Tests;

public class NodeSorterTests
{
    private static NodeSortKey Folder(string name, int index) => new(true, name, true, 0, index);

    private static NodeSortKey Entry(string name, long size, int index, bool named = true) => new(false, name, named, size, index);

    // Same data for every test: two folders and five files, one of them unnamed.
    private static NodeSortKey[] Sample() => new[]
    {
        Folder("zeta", 0),
        Folder("alpha", 1),
        Entry("b.csc", 300, 2),
        Entry("A.xsc", 100, 3),
        Entry("c.csc", 200, 4),
        Entry("0x00000010", 50, 5, named: false),
        Entry("d.xsc", 100, 6),
    };

    private static string[] Order(NodeSortMode mode) =>
        NodeSorter.Sort(Sample(), key => key, mode).Select(key => key.Name).ToArray();

    [Fact]
    public void NameAscending_KnownNamesFirstThenHashes()
    {
        Assert.Equal(new[] { "alpha", "zeta", "A.xsc", "b.csc", "c.csc", "d.xsc", "0x00000010" }, Order(NodeSortMode.NameAscending));
    }

    [Fact]
    public void NameDescending_ReversesNamesButHashesStayLast()
    {
        Assert.Equal(new[] { "zeta", "alpha", "d.xsc", "c.csc", "b.csc", "A.xsc", "0x00000010" }, Order(NodeSortMode.NameDescending));
    }

    [Fact]
    public void SizeAscending_TiesBrokenByName()
    {
        Assert.Equal(new[] { "alpha", "zeta", "0x00000010", "A.xsc", "d.xsc", "c.csc", "b.csc" }, Order(NodeSortMode.SizeAscending));
    }

    [Fact]
    public void SizeDescending_TiesBrokenByName()
    {
        Assert.Equal(new[] { "alpha", "zeta", "b.csc", "c.csc", "A.xsc", "d.xsc", "0x00000010" }, Order(NodeSortMode.SizeDescending));
    }

    [Fact]
    public void TypeAscending_GroupsByExtensionAndPutsNoExtensionLast()
    {
        Assert.Equal(new[] { "alpha", "zeta", "b.csc", "c.csc", "A.xsc", "d.xsc", "0x00000010" }, Order(NodeSortMode.TypeAscending));
    }

    [Fact]
    public void TypeDescending_NoExtensionStillLast()
    {
        Assert.Equal(new[] { "alpha", "zeta", "A.xsc", "d.xsc", "b.csc", "c.csc", "0x00000010" }, Order(NodeSortMode.TypeDescending));
    }

    [Fact]
    public void OriginalOrder_FollowsTheTableOfContents()
    {
        Assert.Equal(new[] { "zeta", "alpha", "b.csc", "A.xsc", "c.csc", "0x00000010", "d.xsc" }, Order(NodeSortMode.OriginalOrder));
    }

    [Fact]
    public void OriginalOrderReversed_FollowsTheTableOfContentsBackwards()
    {
        Assert.Equal(new[] { "alpha", "zeta", "d.xsc", "0x00000010", "c.csc", "A.xsc", "b.csc" }, Order(NodeSortMode.OriginalOrderReversed));
    }

    [Fact]
    public void FoldersAreFirstInEveryMode()
    {
        foreach (NodeSortMode mode in Enum.GetValues<NodeSortMode>())
        {
            NodeSortKey[] result = NodeSorter.Sort(Sample(), key => key, mode).ToArray();

            Assert.True(result[0].IsDirectory, mode.ToString());
            Assert.True(result[1].IsDirectory, mode.ToString());
            Assert.All(result.Skip(2), key => Assert.False(key.IsDirectory, mode.ToString()));
        }
    }

    [Fact]
    public void Sort_KeepsEveryItemExactlyOnce()
    {
        foreach (NodeSortMode mode in Enum.GetValues<NodeSortMode>())
        {
            Assert.Equal(7, NodeSorter.Sort(Sample(), key => key, mode).Count);
        }
    }

    [Theory]
    [InlineData("a.CSC", ".csc")]
    [InlineData("0x00FEB5CC", "")]
    [InlineData("file.", "")]
    [InlineData(".hidden", "")]
    [InlineData("two.dots.xsc", ".xsc")]
    public void Extension_IsLowerCaseWithDotOrEmpty(string name, string expected)
    {
        Assert.Equal(expected, Entry(name, 1, 0).Extension);
    }
}
