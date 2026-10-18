using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicRDR.Core.Session;

/// <summary>An edit that has been applied to the open archive but not yet written by "Save RPF".</summary>
public sealed class SessionEdit
{
    /// <summary>Hash path of the file, e.g. <c>root/0x560A49F7/0x00FEB5CC</c> (stays valid when names change).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>File name (inside the session's edits folder) holding the replacement content.</summary>
    public string File { get; set; } = string.Empty;
}

/// <summary>Everything needed to put the app back exactly where the user left it.</summary>
public sealed class SessionState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Name shown in the header (the picked document's name).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>File name (inside the session folder) of the working copy of the archive.</summary>
    public string ArchiveFile { get; set; } = string.Empty;

    public string DirectoryPath { get; set; } = "root";

    public string SearchText { get; set; } = string.Empty;

    public string? SelectedPath { get; set; }

    public int ScrollIndex { get; set; }

    public List<SessionEdit> Edits { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class SessionJsonContext : JsonSerializerContext
{
}

public static class SessionSerializer
{
    public static string Serialize(SessionState state) =>
        JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState);

    /// <summary>Returns <c>null</c> for damaged or unsupported data instead of throwing.</summary>
    public static SessionState? Deserialize(string json)
    {
        try
        {
            SessionState? state = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState);
            return state is { Version: SessionState.CurrentVersion } ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
