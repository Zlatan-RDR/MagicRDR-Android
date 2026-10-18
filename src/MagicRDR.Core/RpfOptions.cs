namespace MagicRDR.Core;

public sealed class RpfOpenOptions
{
    /// <summary>Force a platform. When <c>null</c> it is detected from the header (same heuristic as the original tool).</summary>
    public RpfPlatform? Platform { get; set; }

    /// <summary>Keep the source stream open when the archive is disposed.</summary>
    public bool LeaveOpen { get; set; }

    /// <summary>Hash → file-name dictionary. RPF6 stores only hashes, so names come from here.</summary>
    public RpfNameDictionary? Names { get; set; }

    /// <summary>TOC encryption key; defaults to <see cref="RpfCrypto.DefaultKey"/>.</summary>
    public byte[]? EncryptionKey { get; set; }
}

public enum RpfCompressionMode
{
    /// <summary>
    /// Compress when the entry being replaced was compressed, or when the file looks like text
    /// (see <see cref="RpfFileTypes.IsTextFile"/>). Resources are never touched.
    /// </summary>
    Auto,

    Always,

    Never,
}

public sealed class RpfImportOptions
{
    public RpfCompressionMode Compression { get; set; } = RpfCompressionMode.Auto;

    /// <summary>Rename the entry (re-hashes the name). <c>null</c> keeps the existing name.</summary>
    public string? NewName { get; set; }

    /// <summary>
    /// When <c>true</c> (default) replacing a resource with a non-resource (or vice versa) is rejected,
    /// since that almost always corrupts the game data. Set to <c>false</c> to reproduce the original tool's behaviour.
    /// </summary>
    public bool StrictResourceCheck { get; set; } = true;
}
