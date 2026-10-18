using System.Text;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class HexDumperTests
{
    [Fact]
    public void Format_ShowsOffsetHexAndAscii()
    {
        string dump = HexDumper.Format(Encoding.ASCII.GetBytes("Hello, RDR!"));

        Assert.StartsWith("00000000  48 65 6C 6C 6F 2C 20 52 ", dump);
        Assert.Contains("|Hello, RDR!|", dump);
        Assert.EndsWith("\n", dump);
    }

    [Fact]
    public void Format_SplitsRowsAndAppliesBaseOffset()
    {
        byte[] data = new byte[20];
        string[] lines = HexDumper.Format(data, baseOffset: 0x100).TrimEnd('\n').Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("00000100  ", lines[0]);
        Assert.StartsWith("00000110  ", lines[1]);
        Assert.Contains("|....|", lines[1]); // 4 bytes on the second row, all non-printable
    }

    [Fact]
    public void Format_EightBytesPerRowKeepsEveryRowShortEnoughForAPhone()
    {
        string[] lines = HexDumper.Format(new byte[20], bytesPerRow: 8).TrimEnd('\n').Split('\n');

        Assert.Equal(3, lines.Length);                     // 8 + 8 + 4 bytes
        Assert.All(lines, line => Assert.True(line.Length <= 46, line));
        Assert.StartsWith("00000008  ", lines[1]);
    }

    [Fact]
    public void Format_EmptyInputGivesEmptyText() => Assert.Equal(string.Empty, HexDumper.Format(ReadOnlySpan<byte>.Empty));
}
