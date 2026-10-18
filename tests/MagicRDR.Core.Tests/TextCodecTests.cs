using System.Text;
using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class TextCodecTests
{
    [Fact]
    public void Utf8_NoBom_RoundTripsExactly()
    {
        byte[] original = Encoding.UTF8.GetBytes("<root>\n  <name>caf\u00E9 \u0645\u0631\u062D\u0628\u0627</name>\n</root>\n");

        TextDocument doc = TextCodec.Decode(original);
        byte[] again = TextCodec.Encode(doc.Text, doc.Format, out bool changed);

        Assert.Equal(TextEncodingKind.Utf8, doc.Format.Encoding);
        Assert.False(doc.Format.HasBom);
        Assert.False(doc.Format.CrLf);
        Assert.False(changed);
        Assert.Equal(original, again);
    }

    [Fact]
    public void Utf8_WithBomAndCrLf_KeepsBothAndEditorSeesLf()
    {
        byte[] original = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("line one\r\nline two\r\n")).ToArray();

        TextDocument doc = TextCodec.Decode(original);

        Assert.True(doc.Format.HasBom);
        Assert.True(doc.Format.CrLf);
        Assert.Equal("line one\nline two\n", doc.Text);
        Assert.Equal(original, TextCodec.Encode(doc.Text, doc.Format, out _));
    }

    [Fact]
    public void Utf16Le_WithBom_RoundTrips()
    {
        byte[] original = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Hello\nWorld")).ToArray();

        TextDocument doc = TextCodec.Decode(original);

        Assert.Equal(TextEncodingKind.Utf16LittleEndian, doc.Format.Encoding);
        Assert.Equal("Hello\nWorld", doc.Text);
        Assert.Equal(original, TextCodec.Encode(doc.Text, doc.Format, out _));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToLatin1_AndRoundTrips()
    {
        byte[] original = { 0x63, 0x61, 0x66, 0xE9, 0x0A }; // "caf\u00E9\n" in Latin-1, not valid UTF-8

        TextDocument doc = TextCodec.Decode(original);

        Assert.Equal(TextEncodingKind.Latin1, doc.Format.Encoding);
        Assert.Equal("caf\u00E9\n", doc.Text);
        Assert.Equal(original, TextCodec.Encode(doc.Text, doc.Format, out bool changed));
        Assert.False(changed);
    }

    [Fact]
    public void Latin1_FileEditedWithArabic_SwitchesToUtf8AndReportsIt()
    {
        TextDocument doc = TextCodec.Decode(new byte[] { 0x63, 0x61, 0x66, 0xE9 });

        byte[] bytes = TextCodec.Encode(doc.Text + " \u0645", doc.Format, out bool changed);

        Assert.True(changed);
        Assert.Equal("caf\u00E9 \u0645", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void TypedLineBreaksFollowTheOriginalStyle()
    {
        TextDocument crlf = TextCodec.Decode(Encoding.ASCII.GetBytes("a\r\nb"));
        Assert.Equal("a\r\nb\r\nc", Encoding.ASCII.GetString(TextCodec.Encode("a\nb\nc", crlf.Format, out _)));

        TextDocument lf = TextCodec.Decode(Encoding.ASCII.GetBytes("a\nb"));
        Assert.Equal("a\nb\nc", Encoding.ASCII.GetString(TextCodec.Encode("a\nb\nc", lf.Format, out _)));
    }

    [Fact]
    public void LooksLikeText_SeparatesTextFromBinary()
    {
        Assert.True(TextCodec.LooksLikeText(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><a/>")));
        Assert.False(TextCodec.LooksLikeText(new byte[] { 0x52, 0x53, 0x43, 0x00, 0x00, 0x01, 0x02, 0x03 })); // "RSC" + NULs
        Assert.False(TextCodec.LooksLikeText(ReadOnlySpan<byte>.Empty));
    }
}
