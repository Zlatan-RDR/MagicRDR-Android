using System.Text;

namespace MagicRDR.Core.Formats;

public enum TextEncodingKind
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,

    /// <summary>Used when the bytes are not valid UTF-8 (every byte maps to one character).</summary>
    Latin1,
}

/// <summary>How a text file was stored, so it can be written back the same way.</summary>
public sealed record TextFormat(TextEncodingKind Encoding, bool HasBom, bool CrLf);

public sealed record TextDocument(string Text, TextFormat Format);

/// <summary>
/// Reads and writes text files for the in-app editor. The editor always works with "\n" line endings;
/// the original encoding, byte-order mark and line-ending style are remembered and restored on save.
/// </summary>
public static class TextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly UnicodeEncoding Utf16Le = new(false, false);
    private static readonly UnicodeEncoding Utf16Be = new(true, false);

    /// <summary>
    /// Heuristic used to decide whether a file can be shown as text: no NUL bytes in the first 4 KB and at least
    /// 90 % printable ASCII (or a UTF-16 byte-order mark followed by text).
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
        {
            return true; // UTF-16 text legitimately contains NUL bytes
        }

        int sample = Math.Min(data.Length, 4096);
        int printable = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 0)
            {
                return false;
            }

            // bytes >= 0x80 count as printable only when they could be part of UTF-8 text
            if ((b >= 0x20 && b < 0x7F) || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || b >= 0xC2)
            {
                printable++;
            }
            else if (b >= 0x80 && b < 0xC2)
            {
                printable++; // UTF-8 continuation bytes
            }
        }

        return printable >= sample * 0.9;
    }

    public static TextDocument Decode(ReadOnlySpan<byte> data)
    {
        int bomLength = 0;
        TextEncodingKind kind;
        bool hasBom = false;
        string text;

        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            kind = TextEncodingKind.Utf8;
            bomLength = 3;
            hasBom = true;
            text = Utf8.GetString(data.Slice(bomLength));
        }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            kind = TextEncodingKind.Utf16LittleEndian;
            bomLength = 2;
            hasBom = true;
            text = Utf16Le.GetString(data.Slice(bomLength));
        }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            kind = TextEncodingKind.Utf16BigEndian;
            bomLength = 2;
            hasBom = true;
            text = Utf16Be.GetString(data.Slice(bomLength));
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(data);
                kind = TextEncodingKind.Utf8;
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(data);
                kind = TextEncodingKind.Latin1;
            }
        }

        bool crLf = text.Contains("\r\n", StringComparison.Ordinal);
        if (crLf)
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        return new TextDocument(text, new TextFormat(kind, hasBom, crLf));
    }

    /// <summary>
    /// Turns edited text back into file bytes using the original format. If the text now contains characters that the
    /// original single-byte encoding cannot hold, it is written as UTF-8 instead and <paramref name="encodingChanged"/> is true.
    /// </summary>
    public static byte[] Encode(string text, TextFormat format, out bool encodingChanged)
    {
        encodingChanged = false;
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (format.CrLf)
        {
            normalized = normalized.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        TextEncodingKind kind = format.Encoding;
        bool hasBom = format.HasBom;

        if (kind == TextEncodingKind.Latin1)
        {
            foreach (char c in normalized)
            {
                if (c > 0xFF)
                {
                    kind = TextEncodingKind.Utf8;
                    hasBom = false;
                    encodingChanged = true;
                    break;
                }
            }
        }

        byte[] body;
        byte[] bom;
        switch (kind)
        {
            case TextEncodingKind.Utf16LittleEndian:
                body = Utf16Le.GetBytes(normalized);
                bom = hasBom ? new byte[] { 0xFF, 0xFE } : Array.Empty<byte>();
                break;
            case TextEncodingKind.Utf16BigEndian:
                body = Utf16Be.GetBytes(normalized);
                bom = hasBom ? new byte[] { 0xFE, 0xFF } : Array.Empty<byte>();
                break;
            case TextEncodingKind.Latin1:
                body = Encoding.Latin1.GetBytes(normalized);
                bom = Array.Empty<byte>();
                break;
            default:
                body = Utf8.GetBytes(normalized);
                bom = hasBom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>();
                break;
        }

        if (bom.Length == 0)
        {
            return body;
        }

        byte[] result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    public static string Describe(TextFormat format)
    {
        string encoding = format.Encoding switch
        {
            TextEncodingKind.Utf8 => format.HasBom ? "UTF-8 (BOM)" : "UTF-8",
            TextEncodingKind.Utf16LittleEndian => "UTF-16 LE",
            TextEncodingKind.Utf16BigEndian => "UTF-16 BE",
            _ => "Latin-1",
        };

        return $"{encoding} · {(format.CrLf ? "CRLF" : "LF")}";
    }
}
