using MagicRDR.Core.Updates;
using Xunit;

namespace MagicRDR.Core.Tests;

public class UpdateParserTests
{
    private const string Sha64 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha32 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string Release(string tag = "v1.1.3", bool draft = false, bool prerelease = false, string? digest64 = null, string? digest32 = null)
    {
        string d64 = digest64 == null ? string.Empty : $", \"digest\": \"{digest64}\"";
        string d32 = digest32 == null ? string.Empty : $", \"digest\": \"{digest32}\"";
        return "{"
            + $"\"tag_name\": \"{tag}\", \"draft\": {draft.ToString().ToLowerInvariant()}, \"prerelease\": {prerelease.ToString().ToLowerInvariant()},"
            + "\"body\": \"### What's new / x\\n\\n- Faster start. / Arabic: \\u0633\\u0631\\u064a\\u0639\\n- Names list updated.\\n\\nCommits in this build:\\n- ignored\\n\\n"
            + $"- 32-bit APK file SHA-256: `{Sha32}`\\n- APK file SHA-256: `{Sha64}`\","
            + "\"assets\": ["
            + $"{{\"name\": \"MagicRDR-Android-v1.1.3-armeabi-v7a.apk\", \"browser_download_url\": \"https://example.test/v7a.apk\", \"size\": 100, \"state\": \"uploaded\"{d32}}},"
            + $"{{\"name\": \"MagicRDR-Android-v1.1.3.apk\", \"browser_download_url\": \"https://example.test/main.apk\", \"size\": 200, \"state\": \"uploaded\"{d64}}}"
            + "]}";
    }

    [Theory]
    [InlineData("1.1.3", true, "1.1.3")]
    [InlineData("v1.1.3", true, "1.1.3")]
    [InlineData("V2.0", true, "2.0")]
    [InlineData("1.2.0-beta", true, "1.2.0")]
    [InlineData("", false, "0.0")]
    [InlineData("latest", false, "0.0")]
    [InlineData("7", false, "0.0")]
    public void TryParseVersion_ReadsTags(string text, bool expected, string expectedVersion)
    {
        bool ok = UpdateParser.TryParseVersion(text, out Version version);

        Assert.Equal(expected, ok);
        Assert.Equal(expectedVersion, version.ToString());
    }

    [Fact]
    public void Parse_NewerRelease_PicksTheSixtyFourBitApk()
    {
        UpdateInfo? update = UpdateParser.Parse(Release(), new Version(1, 1, 2), is64Bit: true);

        Assert.NotNull(update);
        Assert.Equal("v1.1.3", update!.Tag);
        Assert.Equal("1.1.3", update.VersionText);
        Assert.Equal("MagicRDR-Android-v1.1.3.apk", update.AssetName);
        Assert.Equal("https://example.test/main.apk", update.DownloadUrl);
        Assert.Equal(200, update.Size);
        Assert.Equal(Sha64, update.Sha256);
    }

    [Fact]
    public void Parse_NewerRelease_PicksTheThirtyTwoBitApkForA32BitProcess()
    {
        UpdateInfo? update = UpdateParser.Parse(Release(), new Version(1, 1, 2), is64Bit: false);

        Assert.NotNull(update);
        Assert.Equal("MagicRDR-Android-v1.1.3-armeabi-v7a.apk", update!.AssetName);
        Assert.Equal(100, update.Size);
        Assert.Equal(Sha32, update.Sha256);
    }

    [Fact]
    public void Parse_PrefersTheDigestOfTheAsset()
    {
        string digest = "sha256:" + new string('C', 64);

        UpdateInfo? update = UpdateParser.Parse(Release(digest64: digest), new Version(1, 1, 2), is64Bit: true);

        Assert.Equal(new string('c', 64), update!.Sha256);
    }

    [Theory]
    [InlineData("1.1.3")]
    [InlineData("1.1.4")]
    [InlineData("1.2.0")]
    public void Parse_SameOrOlderRelease_IsNotAnUpdate(string installed)
    {
        UpdateInfo? update = UpdateParser.Parse(Release(), Version.Parse(installed), is64Bit: true);

        Assert.Null(update);
    }

    [Fact]
    public void Parse_DraftAndPreRelease_AreIgnored()
    {
        Assert.Null(UpdateParser.Parse(Release(draft: true), new Version(1, 1, 2), is64Bit: true));
        Assert.Null(UpdateParser.Parse(Release(prerelease: true), new Version(1, 1, 2), is64Bit: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": \"v9.9.9\"}")]
    [InlineData("{\"tag_name\": \"v9.9.9\", \"assets\": []}")]
    public void Parse_BrokenOrEmptyAnswers_GiveNoUpdateAndDoNotThrow(string json)
    {
        Assert.Null(UpdateParser.Parse(json, new Version(1, 1, 2), is64Bit: true));
    }

    [Fact]
    public void ExtractWhatsNew_KeepsEnglishBulletsOnly()
    {
        string notes = "## Magic RDR 1.1.3\n\n### What's new / x\n\n- Faster start. / \u0633\u0631\u064a\u0639\n- Names list updated.\n- \u0639\u0631\u0628\u064a \u0641\u0642\u0637\n\nCommits in this build:\n- hidden commit\n";

        string? text = UpdateParser.ExtractWhatsNew(notes);

        Assert.Equal("\u2022 Faster start.\n\u2022 Names list updated.", text);
    }

    [Fact]
    public void ExtractWhatsNew_WithoutTheSection_IsNull()
    {
        Assert.Null(UpdateParser.ExtractWhatsNew("### Verify before installing\n- something"));
        Assert.Null(UpdateParser.ExtractWhatsNew(null));
    }
}
