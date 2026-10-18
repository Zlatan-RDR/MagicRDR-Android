namespace MagicRDR.App.Services;

/// <summary>A file chosen by the user. On Android this is usually a <c>content://</c> document, so only a stream is available.</summary>
public sealed class PickedFile
{
    private readonly Func<Task<Stream>> _open;

    public PickedFile(string name, Func<Task<Stream>> open, string? temporaryPath = null)
    {
        Name = name;
        _open = open;
        TemporaryPath = temporaryPath;
    }

    public string Name { get; }

    /// <summary>
    /// Where the system picker put its own copy of the file (Android copies a chosen document into the app's cache).
    /// That copy is deleted as soon as the file has been read, otherwise every opened file would stay on the phone twice.
    /// </summary>
    public string? TemporaryPath { get; }

    public Task<Stream> OpenReadAsync() => _open();
}

public sealed record ExportResult(bool Success, string? Error = null);

/// <summary>Everything the view model needs from the platform's storage, so it stays free of MAUI types.</summary>
public interface IFileService
{
    /// <summary>App-private scratch folder (cleared at start-up). Always a real, seekable file-system path.</summary>
    string WorkDirectory { get; }

    /// <summary>App-private folder that survives restarts (used to remember imported names).</summary>
    string DataDirectory { get; }

    /// <summary>
    /// Folder that keeps the open archive, your pending edits and the saved screen position, so the app can
    /// come back exactly where you left it. Unlike <see cref="WorkDirectory"/> it is never cleaned at start-up.
    /// </summary>
    string SessionDirectory { get; }

    void CleanWorkDirectory();

    /// <summary>Deletes the copies the system pickers left in the app's cache folder. Safe to call whenever no picked file is still waiting to be read.</summary>
    void CleanCache();

    /// <summary>Shows the system file picker. Returns <c>null</c> if the user cancels.</summary>
    Task<PickedFile?> PickAsync(string title);

    /// <summary>Shows the system file picker and lets the user choose several files. Returns an empty list if the user cancels.</summary>
    Task<IReadOnlyList<PickedFile>> PickManyAsync(string title);

    /// <summary>
    /// Copies a picked document to <paramref name="targetPath"/> (a real file-system path, overwritten if it exists).
    /// Needed because Android document streams are often not seekable, and RPF parsing needs random access.
    /// A partly written target is deleted if the copy fails or is cancelled.
    /// </summary>
    Task CopyToAsync(PickedFile file, string targetPath, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Lets the user choose a destination (system "save as" dialog) and writes <paramref name="content"/> there.</summary>
    Task<ExportResult> SaveAsync(string suggestedName, Stream content, CancellationToken cancellationToken);

    /// <summary>Opens a file bundled with the app (Resources/Raw). Returns <c>null</c> if it does not exist.</summary>
    Task<Stream?> OpenPackageFileAsync(string name);
}

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}
