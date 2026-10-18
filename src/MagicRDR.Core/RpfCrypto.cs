using System.Security.Cryptography;

namespace MagicRDR.Core;

/// <summary>
/// AES-256 (ECB, no padding) applied 16 times in a row, which is how RPF6 protects its
/// table of contents. Ported from <c>DataUtils.Encrypt/Decrypt</c>.
/// </summary>
public static class RpfCrypto
{
    private const int Rounds = 16;

    /// <summary>The key used by the original tool (taken verbatim from <c>AppGlobals.EncryptionKey</c>).</summary>
    public static readonly byte[] DefaultKey =
    {
        0xB7, 0x62, 0xDF, 0xB6, 0xE2, 0xB2, 0xC6, 0xDE, 0xAF, 0x72, 0x2A, 0x32, 0xD2, 0xFB, 0x6F, 0x0C,
        0x98, 0xA3, 0x21, 0x74, 0x62, 0xC9, 0xC4, 0xED, 0xAD, 0xAA, 0x2E, 0xD0, 0xDD, 0xF9, 0x2F, 0x10
    };

    public static byte[] Decrypt(byte[] data, byte[]? key = null) => Transform(data, key ?? DefaultKey, decrypt: true);

    public static byte[] Encrypt(byte[] data, byte[]? key = null) => Transform(data, key ?? DefaultKey, decrypt: false);

    private static byte[] Transform(byte[] data, byte[] key, bool decrypt)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(key);

        // Work on a copy; bytes past the last full 16-byte block are left untouched (as in the original).
        byte[] result = (byte[])data.Clone();
        int length = result.Length & ~15;
        if (length == 0)
        {
            return result;
        }

        using Aes aes = Aes.Create();
        aes.Key = key;

        byte[] scratch = new byte[length];
        for (int round = 0; round < Rounds; round++)
        {
            ReadOnlySpan<byte> input = result.AsSpan(0, length);
            if (decrypt)
            {
                aes.DecryptEcb(input, scratch, PaddingMode.None);
            }
            else
            {
                aes.EncryptEcb(input, scratch, PaddingMode.None);
            }

            scratch.AsSpan(0, length).CopyTo(result);
        }

        return result;
    }
}
