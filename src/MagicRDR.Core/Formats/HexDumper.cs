using System.Text;

namespace MagicRDR.Core.Formats;

/// <summary>
/// Classic hex dump (offset, hex bytes, ASCII). Pure logic with no UI, so the same code will back the Android
/// hex viewer and can be unit-tested on the build server. It is the first "format helper" of the feature-parity work.
/// </summary>
public static class HexDumper
{
    public static string Format(ReadOnlySpan<byte> data, long baseOffset = 0, int bytesPerRow = 16)
    {
        if (bytesPerRow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerRow));
        }

        var text = new StringBuilder();
        for (int row = 0; row < data.Length; row += bytesPerRow)
        {
            int count = Math.Min(bytesPerRow, data.Length - row);
            text.Append((baseOffset + row).ToString("X8")).Append("  ");

            for (int i = 0; i < bytesPerRow; i++)
            {
                if (i < count)
                {
                    text.Append(data[row + i].ToString("X2")).Append(' ');
                }
                else
                {
                    text.Append("   ");
                }

                if (i == 7 && bytesPerRow > 8)
                {
                    text.Append(' ');
                }
            }

            text.Append(" |");
            for (int i = 0; i < count; i++)
            {
                byte b = data[row + i];
                text.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            text.Append("|\n");
        }

        return text.ToString();
    }
}
