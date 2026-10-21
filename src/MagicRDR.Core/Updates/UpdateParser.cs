using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagicRDR.Core.Updates;

/// <summary>A newer release that can be installed on this device.</summary>
/// <param name="Tag">Git tag of the release, for example <c>v1.1.3</c>.</param>
/// <param name="Version">Parsed version of the release.</param>
/// <param name="VersionText">Version as text without the leading "v", for example <c>1.1.3</c>.</param>
/// <param name="AssetName">File name of the APK that matches this device (64-bit or 32-bit).</param>
/// <param name="DownloadUrl">Direct download address of that APK.</param>
/// <param name="Size">Size of the APK in bytes, or 0 when unknown.</param>
/// <param name="Sha256">Expected SHA-256 of the APK (lower-case hex), or <c>null</c> when the release does not state it.</param>
/// <param name="WhatsNew">English bullet list taken from the release notes, or <c>null</c>.</param>
public sealed record UpdateInfo(
    string Tag,
    Version Version,
    string VersionText,
    string AssetName,
    string DownloadUrl,
    long Size,
    string? Sha256,
    string? WhatsNew);

/// <summary>
/// Reads the answer of GitHub's "latest release" API and decides whether it is an update for this device.
/// It does no network or file work, so it can be tested on its own.
/// </summary>
public static class UpdateParser
{
    // The release notes written by the build pipeline state both file hashes. The 32-bit line is the more specific one.
    private static readonly Regex Sha64Line = new(@"(?<!32-bit )APK file SHA-256:\s*`?([0-9a-fA-F]{64})", RegexOptions.Compiled);
    private static readonly Regex Sha32Line = new(@"32-bit APK file SHA-256:\s*`?([0-9a-fA-F]{64})", RegexOptions.Compiled);

    /// <summary>Reads "1.1.3", "v1.1.3" or "1.1.3-beta" as a version. Needs at least two numbers.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed[0] == 'v' || trimmed[0] == 'V')
        {
            trimmed = trimmed.Substring(1);
        }

        int suffix = trimmed.IndexOfAny(new[] { '-', '+', ' ' });
        if (suffix >= 0)
        {
            trimmed = trimmed.Substring(0, suffix);
        }

        if (!Version.TryParse(trimmed, out Version? parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>
    /// Returns the update to offer, or <c>null</c> when the release is not newer than <paramref name="current"/>,
    /// is a draft or pre-release, has no APK for this device, or the answer cannot be understood.
    /// </summary>
    /// <param name="json">Body of <c>GET /repos/{owner}/{repo}/releases/latest</c>.</param>
    /// <param name="current">Version of the installed app.</param>
    /// <param name="is64Bit">True when the app runs as a 64-bit process (picks the 64-bit APK, otherwise the armeabi-v7a APK).</param>
    public static UpdateInfo? Parse(string json, Version current, bool is64Bit)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(current);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return null;
            }

            string? tag = GetString(root, "tag_name");
            if (tag == null || !TryParseVersion(tag, out Version remote) || remote <= current)
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string body = GetString(root, "body") ?? string.Empty;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = GetString(asset, "name");
                string? url = GetString(asset, "browser_download_url");
                if (name == null || url == null || !name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? state = GetString(asset, "state");
                if (state != null && !string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool is32BitApk = name.Contains("armeabi-v7a", StringComparison.OrdinalIgnoreCase);
                if (is32BitApk == is64Bit)
                {
                    continue; // a 64-bit process takes the normal APK, a 32-bit one takes the armeabi-v7a APK
                }

                long size = 0;
                if (asset.TryGetProperty("size", out JsonElement sizeElement) &&
                    sizeElement.ValueKind == JsonValueKind.Number &&
                    sizeElement.TryGetInt64(out long parsedSize))
                {
                    size = parsedSize;
                }

                string? sha = ExtractSha256(GetString(asset, "digest"), body, is32BitApk);
                return new UpdateInfo(tag.Trim(), remote, remote.ToString(), name, url, size, sha, ExtractWhatsNew(body));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes the bullet points under "What's new" from the release notes, in English only
    /// (the Arabic half of a bilingual bullet is cut off). Returns <c>null</c> when there are none.
    /// </summary>
    public static string? ExtractWhatsNew(string? body, int maxLines = 8)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        bool inSection = false;
        var lines = new List<string>();
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            if (!inSection)
            {
                if (line.StartsWith("###", StringComparison.Ordinal) && line.Contains("What's new", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                }

                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith("Commits in this build", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Full comparison", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            string text = KeepEnglishPart(line.Substring(2).Trim());
            if (text.Length == 0)
            {
                continue;
            }

            lines.Add("\u2022 " + text);
            if (lines.Count >= maxLines)
            {
                break;
            }
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string KeepEnglishPart(string text)
    {
        int arabic = text.IndexOfAny(ArabicLetters);
        if (arabic < 0)
        {
            return text;
        }

        string head = text.Substring(0, arabic).TrimEnd();
        if (head.EndsWith('/'))
        {
            head = head.Substring(0, head.Length - 1).TrimEnd();
        }

        return head;
    }

    private static readonly char[] ArabicLetters = BuildArabicLetters();

    private static char[] BuildArabicLetters()
    {
        var letters = new char[0x06FF - 0x0600 + 1];
        for (int i = 0; i < letters.Length; i++)
        {
            letters[i] = (char)(0x0600 + i);
        }

        return letters;
    }

    private static string? ExtractSha256(string? digest, string body, bool is32BitApk)
    {
        // GitHub states a digest for every uploaded file ("sha256:<hex>").
        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string hex = digest.Substring(7).Trim();
            if (hex.Length == 64)
            {
                return hex.ToLowerInvariant();
            }
        }

        // Otherwise use the hash printed in the release notes by the build pipeline.
        Match match = (is32BitApk ? Sha32Line : Sha64Line).Match(body);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
