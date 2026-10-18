using System.Text;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class StrtblTests
{
    private static byte[] Block(byte tag)
    {
        // every entry record starts with the 1.0f marker (00 00 80 3F), then 16 more bytes
        byte[] block = new byte[20];
        block[2] = 0x80;
        block[3] = 0x3F;
        for (int i = 4; i < 20; i++)
        {
            block[i] = (byte)(tag + i);
        }

        return block;
    }

    /// <summary>English, Spanish, French and German (index 3 is the first Unicode language).</summary>
    private static StrtblDocument Sample()
    {
        var doc = new StrtblDocument { NameBlock = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } };
        string[][] texts =
        {
            new[] { "Hello", "Second line", "Third" },
            new[] { "Hola", "Segunda", "Tercera" },
            new[] { "Bonjour", "Deuxi\u00E8me", "Troisi\u00E8me" },
            new[] { "Hallo", "Zweite", "Dritte \u0645" },
        };

        for (int i = 0; i < texts.Length; i++)
        {
            var segment = new StrtblSegment { Language = StrtblCodec.LanguageNames[i], UnknownValue = (uint)(100 + i) };
            for (int s = 0; s < texts[i].Length; s++)
            {
                segment.Strings.Add(texts[i][s]);
                segment.EntryBlocks.Add(Block((byte)(i * 16 + s)));
            }

            doc.Segments.Add(segment);
        }

        return doc;
    }

    [Fact]
    public void Write_ThenRead_ReturnsEveryLanguageAndText()
    {
        StrtblDocument original = Sample();
        byte[] bytes = StrtblCodec.Write(original);

        Assert.True(StrtblCodec.TryRead(bytes, out StrtblDocument? back, out string? error), error);
        Assert.Equal(4, back!.Segments.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(original.Segments[i].Strings, back.Segments[i].Strings);
            Assert.Equal(original.Segments[i].UnknownValue, back.Segments[i].UnknownValue);
        }

        Assert.Equal(original.NameBlock, back.NameBlock);
        Assert.Equal("English", back.Segments[0].Language);
    }

    [Fact]
    public void RoundTripsExactly_IsTrueForOurOwnOutput_AndRemembersPadding()
    {
        byte[] bytes = StrtblCodec.Write(Sample());
        byte[] padded = bytes.Concat(Enumerable.Repeat((byte)0xCD, 24)).ToArray();

        Assert.True(StrtblCodec.TryRead(padded, out StrtblDocument? doc, out _));
        Assert.True(StrtblCodec.RoundTripsExactly(padded, doc!));
        Assert.Equal(24, doc!.Trailer.Length);
        Assert.Equal(padded, StrtblCodec.Write(doc));
    }

    [Fact]
    public void RoundTripsExactly_IsFalseWhenTheFileHasAnythingWeWouldNotReproduce()
    {
        byte[] bytes = StrtblCodec.Write(Sample());
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = 0x7F; // data after the table that is neither zero nor 0xCD padding

        Assert.True(StrtblCodec.TryRead(bytes, out StrtblDocument? doc, out _));
        Assert.False(StrtblCodec.RoundTripsExactly(bytes, doc!));
    }

    [Fact]
    public void EditedText_IsWrittenAndReadBack()
    {
        StrtblDocument doc = Sample();
        doc.Segments[3].Strings[1] = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"; // German slot is Unicode
        doc.Segments[0].Strings[0] = "Edited";

        Assert.True(StrtblCodec.TryRead(StrtblCodec.Write(doc), out StrtblDocument? back, out _));

        Assert.Equal("Edited", back!.Segments[0].Strings[0]);
        Assert.Equal("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", back.Segments[3].Strings[1]);
        Assert.Equal("Third", back.Segments[0].Strings[2]);      // neighbours untouched
        Assert.Equal(doc.Segments[1].Strings, back.Segments[1].Strings);
    }

    [Fact]
    public void Validate_RejectsCharactersASingleByteLanguageCannotHold()
    {
        StrtblDocument doc = Sample();
        Assert.Null(StrtblCodec.Validate(doc));      // Latin-1 accents are fine

        doc.Segments[1].Strings[0] = "\u0645";       // Arabic in the Spanish slot
        string? problem = StrtblCodec.Validate(doc);

        Assert.NotNull(problem);
        Assert.Contains("Spanish", problem);
        Assert.Contains("U+0645", problem);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(13, true)]
    public void IsUnicode_FollowsTheOriginalLanguageTable(int index, bool unicode) =>
        Assert.Equal(unicode, StrtblCodec.IsUnicode(index));

    [Fact]
    public void TryRead_RejectsFilesThatAreNotLanguageTables()
    {
        Assert.False(StrtblCodec.TryRead(new byte[64], out _, out string? zero));
        Assert.Contains("segment count", zero);

        Assert.False(StrtblCodec.TryRead(Encoding.ASCII.GetBytes("<xml>just some plain text, not a table</xml>"), out _, out _));
        Assert.False(StrtblCodec.TryRead(new byte[] { 1, 0, 0, 0 }, out _, out _));
    }
}

public class StringTableTextTests
{
    [Fact]
    public void Export_ThenParse_RoundTripsTextsWithSpecialCharacters()
    {
        var entries = new List<StringTableEntry>
        {
            new(0xAAAA0001, 0, "line one\nline two"),
            new(0x0000000A, 0, "tab\there and back\\slash"),
            new(0x00000005, 0, string.Empty),
        };

        var rejected = new List<string>();
        List<StringTableTextLine> lines = StringTableText.Parse(StringTableText.ExportHashTable(entries).Split('\n'), rejected);

        Assert.Empty(rejected);
        Assert.Equal(3, lines.Count);
        Assert.Equal(new StringTableTextLine(2, 0, 0xAAAA0001, "line one\nline two"), lines[0]);
        Assert.Equal("tab\there and back\\slash", lines[1].Value);
        Assert.Equal(string.Empty, lines[2].Value);
        Assert.Equal(5u, lines[2].Key);
    }

    [Fact]
    public void Parse_AcceptsLanguageLinesWithoutKey_AndReportsBadLines()
    {
        string[] input =
        {
            "# comment",
            "[0] - Hello",
            "",
            "[12] - Arabic \u0645\u0631\u062D\u0628\u0627",
            "this line is wrong",
            "[x] - nope",
        };
        var rejected = new List<string>();

        List<StringTableTextLine> lines = StringTableText.Parse(input, rejected);

        Assert.Equal(2, lines.Count);
        Assert.Equal(12, lines[1].Index);
        Assert.Null(lines[1].Key);
        Assert.Equal(2, rejected.Count);
        Assert.Contains("line 5", rejected[0]);
    }

    [Fact]
    public void ExportLanguage_WritesOneLinePerText()
    {
        string text = StringTableText.ExportLanguage("Russian", new[] { "a", "b\nc" });

        Assert.Contains("language: Russian", text);
        Assert.Contains("[0] - a\n", text);
        Assert.Contains("[1] - b\\nc\n", text);
    }
}
