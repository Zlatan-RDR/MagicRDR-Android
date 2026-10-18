using System.Text;
using MagicRDR.Core.Session;
using Xunit;

namespace MagicRDR.Core.Tests;

public class SessionTests
{
    [Fact]
    public void SessionState_RoundTripsThroughJson()
    {
        var state = new SessionState
        {
            DisplayName = "strings_d11generic.rpf",
            ArchiveFile = "archive-abc.rpf",
            DirectoryPath = "root/0x560A49F7",
            SearchText = "0x00",
            SelectedPath = "root/0x560A49F7/0x00FEB5CC",
            ScrollIndex = 42,
        };
        state.Edits.Add(new SessionEdit { Path = "root/0x560A49F7/0x017D179B", File = "e1.bin" });

        SessionState? back = SessionSerializer.Deserialize(SessionSerializer.Serialize(state));

        Assert.NotNull(back);
        Assert.Equal("strings_d11generic.rpf", back!.DisplayName);
        Assert.Equal("root/0x560A49F7", back.DirectoryPath);
        Assert.Equal(42, back.ScrollIndex);
        Assert.Single(back.Edits);
        Assert.Equal("e1.bin", back.Edits[0].File);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"Version\": 99 }")]
    public void DamagedOrUnknownSession_IsIgnoredInsteadOfThrowing(string json)
    {
        Assert.Null(SessionSerializer.Deserialize(json));
    }

    [Fact]
    public async Task HashPath_FindsTheSameNodeAgain_EvenAfterNamesAreImported()
    {
        byte[] bytes = SyntheticRpf.Build(
            new SyntheticRpf.Item("a.txt", Encoding.UTF8.GetBytes("hello"), Compressed: false));

        var names = new RpfNameDictionary();
        using Rpf6Archive archive = await Rpf6Archive.OpenAsync(new MemoryStream(bytes), new RpfOpenOptions { Names = names });
        RpfNode node = archive.Root.FindChild(RpfHash.GetHash("a.txt"))!;

        string beforeNames = node.GetHashPath();
        names.Add("a.txt"); // the display name changes, the hash path must not
        string afterNames = node.GetHashPath();

        Assert.Equal(beforeNames, afterNames);
        Assert.StartsWith("root/0x", afterNames);
        Assert.Same(node, archive.FindByPath(afterNames));
    }
}
