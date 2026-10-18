using MagicRDR.Core.Formats;
using Xunit;

namespace MagicRDR.Core.Tests;

public class ArabicVisualCodecTests
{
    // "Bring them back!" - the first example of the table in 0x00FEB5CC (entry #2).
    private const string BringThemBack = "أعيدوهم!";
    private const string BringThemBackVisual = "!\uFEE2\uFEEB\uFEED\uFEAA\uFEF4\uFECB\uFE83";

    [Fact]
    public void Encode_ShapesLettersAndReversesTheLine()
    {
        Assert.Equal(BringThemBackVisual, ArabicVisualCodec.Encode(BringThemBack));
    }

    [Fact]
    public void Decode_RestoresNaturalOrderAndBaseLetters()
    {
        Assert.Equal(BringThemBack, ArabicVisualCodec.Decode(BringThemBackVisual));
    }

    [Fact]
    public void Entry0_WhatAreYouDoing_ShapesAndRoundTrips()
    {
        const string logical = "مالذي تفعله؟ تراجع!";
        const string visual = "!\uFECA\uFE9F\uFE8D\uFEAE\uFE97 \u061F\uFEEA\uFEE0\uFECC\uFED4\uFE97 \uFEF1\uFEAC\uFEDF\uFE8E\uFEE3";

        Assert.Equal(visual, ArabicVisualCodec.Encode(logical));
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }

    [Fact]
    public void Tags_And_Parentheses_AreKeptIntactAndMirrored()
    {
        // Entry #68 style: <red> tags around a parenthesised Arabic name, full stop at the end.
        const string logical = "تهديمن عصابة <red>(ريتودن)<red>.";
        string visual = ArabicVisualCodec.Encode(logical);

        Assert.StartsWith(".<red>(", visual);
        Assert.Contains(")<red> ", visual);
        Assert.DoesNotContain(">der<", visual);
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }

    [Fact]
    public void LineBreaks_StayInPlace_AndEachLineIsHandledOnItsOwn()
    {
        const string logical = "مرحبا\r\nأهلا بك\r\nOK 123 نعم";
        string visual = ArabicVisualCodec.Encode(logical);

        string[] lines = visual.Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.Equal("\uFE8E\uFE92\uFEA3\uFEAE\uFEE3", lines[0]);   // مرحبا, reversed and shaped
        Assert.EndsWith(" OK 123", lines[2]);
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }

    [Fact]
    public void LeftToRightTokens_AreNeverReversed()
    {
        const string logical = "اضغط [A] للمتابعة %s ~r~الآن~w~ \\n نهاية {0}";
        string visual = ArabicVisualCodec.Encode(logical);

        Assert.Contains("[A]", visual);
        Assert.Contains("%s", visual);
        Assert.Contains("~r~", visual);
        Assert.Contains("~w~", visual);
        Assert.Contains("\\n", visual);
        Assert.Contains("{0}", visual);
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }

    [Fact]
    public void Numbers_And_LatinWords_KeepTheirOrder()
    {
        const string logical = "ملك، وزير: Bob's farm 12:30 نعم 50%";
        string visual = ArabicVisualCodec.Encode(logical);

        Assert.Contains("Bob's farm", visual);
        Assert.Contains("12:30", visual);
        Assert.StartsWith("50%", visual);
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }

    [Fact]
    public void LamAlef_IsOneLigatureGlyph_AndIsUnwrappedAgain()
    {
        Assert.Equal("\uFEFB", ArabicVisualCodec.Encode("لا"));
        Assert.Equal("لا", ArabicVisualCodec.Decode("\uFEFB"));

        // "الله": alef, lam, lam, heh - no ligature, because the alef comes first.
        Assert.Equal("\uFEEA\uFEE0\uFEDF\uFE8D", ArabicVisualCodec.Encode("الله"));
        Assert.Equal("الله", ArabicVisualCodec.Decode("\uFEEA\uFEE0\uFEDF\uFE8D"));
    }

    [Fact]
    public void Diacritics_StayWithTheirLetter()
    {
        const string logical = "مَرْحَبًا";
        string visual = ArabicVisualCodec.Encode(logical);

        Assert.Equal("\uFE8E\uFE92\u064B\uFEA3\u064E\uFEAE\u0652\uFEE3\u064E", visual);
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }

    [Theory]
    [InlineData("Hello <red>World</red>")]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("Press (A) to continue.\r\nThen wait.")]
    public void TextWithoutArabic_IsReturnedUnchanged(string text)
    {
        Assert.Equal(text, ArabicVisualCodec.Encode(text));
        Assert.Equal(text, ArabicVisualCodec.Decode(text));
    }

    [Fact]
    public void Decode_LeavesArabicThatIsAlreadyLogicalAlone()
    {
        Assert.Equal("مرحبا بالعالم", ArabicVisualCodec.Decode("مرحبا بالعالم"));
    }

    [Fact]
    public void Encode_NeverReversesStoredTextASecondTime()
    {
        Assert.Equal(BringThemBackVisual, ArabicVisualCodec.Encode(BringThemBackVisual));
    }

    [Fact]
    public void NullIsTreatedAsEmpty()
    {
        Assert.Equal(string.Empty, ArabicVisualCodec.Encode(null));
        Assert.Equal(string.Empty, ArabicVisualCodec.Decode(null));
        Assert.False(ArabicVisualCodec.HasPresentationForms(null));
    }

    [Fact]
    public void Detection_FindsPresentationForms()
    {
        Assert.True(ArabicVisualCodec.HasPresentationForms(BringThemBackVisual));
        Assert.False(ArabicVisualCodec.HasPresentationForms(BringThemBack));
        Assert.True(ArabicVisualCodec.ContainsArabic(BringThemBack));
        Assert.False(ArabicVisualCodec.ContainsArabic("abc 123"));
    }

    [Theory]
    [InlineData("أعيدوهم!")]
    [InlineData("مالذي تفعله؟ تراجع!")]
    [InlineData("تهديمن عصابة <red>(ريتودن)<red>.")]
    [InlineData("سطر\nثان\r\nثالث")]
    [InlineData("ثلاثة 3 أعداء")]
    [InlineData("گ پ چ ژ")]
    [InlineData("ء ئ ؤ آ ى ة")]
    [InlineData("لا إله إلا الله")]
    [InlineData("<red>نص</red> و <blue>نص آخر</blue>")]
    [InlineData("(نص) [نص] {نص} «نص»")]
    public void RoundTrip_IsLossless(string logical)
    {
        string visual = ArabicVisualCodec.Encode(logical);

        Assert.NotEqual(logical, visual);
        Assert.Equal(logical, ArabicVisualCodec.Decode(visual));
    }
}
