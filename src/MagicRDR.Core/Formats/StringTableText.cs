using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MagicRDR.Core.Formats;

/// <summary>One line of an imported translation file.</summary>
public readonly record struct StringTableTextLine(int LineNumber, int Index, uint? Key, string Value);

/// <summary>
/// Plain-text exchange format for translating outside the app: one entry per line,
/// <c>[index] - 0xKEY - text</c> for hash tables and <c>[index] - text</c> for language tables. Line breaks, tabs and
/// backslashes inside a text are escaped (<c>\n</c>, <c>\t</c>, <c>\\</c>). Lines starting with <c>#</c> are comments.
/// Entries are matched by <b>index</b>, so editing the text of a line is all a translator has to do.
/// </summary>
public static partial class StringTableText
{
    [GeneratedRegex(@"^\[(\d+)\]\s-\s(?:(0[xX][0-9A-Fa-f]{1,8})\s-\s)?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            text.Append(c switch { '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        }

        return text.ToString();
    }

    public static string Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\\'))
        {
            return value ?? string.Empty;
        }

        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                char next = value[i + 1];
                switch (next)
                {
                    case 'n': text.Append('\n'); i++; continue;
                    case 'r': text.Append('\r'); i++; continue;
                    case 't': text.Append('\t'); i++; continue;
                    case '\\': text.Append('\\'); i++; continue;
                }
            }

            text.Append(value[i]);
        }

        return text.ToString();
    }

    public static string ExportHashTable(IReadOnlyList<StringTableEntry> entries)
    {
        var text = new StringBuilder(entries.Count * 48);
        text.Append("# Magic RDR string table: [index] - key - text   (edit only the text)\n");
        for (int i = 0; i < entries.Count; i++)
        {
            text.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("] - 0x")
                .Append(entries[i].Key.ToString("X8", CultureInfo.InvariantCulture)).Append(" - ")
                .Append(Escape(entries[i].Value)).Append('\n');
        }

        return text.ToString();
    }

    public static string ExportLanguage(string language, IReadOnlyList<string> strings)
    {
        var text = new StringBuilder(strings.Count * 48);
        text.Append("# Magic RDR string table, language: ").Append(language).Append("   ([index] - text)\n");
        for (int i = 0; i < strings.Count; i++)
        {
            text.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("] - ")
                .Append(Escape(strings[i])).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Parses an exported/translated file. Lines that do not match the format are returned in <paramref name="rejected"/>.</summary>
    public static List<StringTableTextLine> Parse(IEnumerable<string?> lines, List<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(rejected);

        var result = new List<StringTableTextLine>();
        int number = 0;
        foreach (string? raw in lines)
        {
            number++;
            string line = (raw ?? string.Empty).TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            Match match = LinePattern().Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                rejected.Add($"line {number}: not in the \"[index] - text\" format");
                continue;
            }

            uint? key = null;
            if (match.Groups[2].Success)
            {
                key = uint.Parse(match.Groups[2].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            result.Add(new StringTableTextLine(number, index, key, Unescape(match.Groups[3].Value)));
        }

        return result;
    }
}
