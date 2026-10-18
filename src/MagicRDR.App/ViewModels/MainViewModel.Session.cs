using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MagicRDR.App.Models;
using MagicRDR.Core;
using MagicRDR.Core.Session;

namespace MagicRDR.App.ViewModels;

/// <summary>
/// Remembers the open archive and where the user was, so the app comes back exactly as it was left.
/// <para>
/// Everything lives in <c>IFileService.SessionDirectory</c>: the working copy of the archive
/// (<c>archive-*.rpf</c>), the pending edits (<c>edits/*.bin</c>) and <c>state.json</c> (current folder, selection,
/// filter text, scroll position and which file each edit belongs to).
/// </para>
/// </summary>
public partial class MainViewModel
{
    private readonly Dictionary<string, string> _edits = new();   // hash path -> file name in the edits folder
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private string? _sessionArchiveFile;
    private CancellationTokenSource? _saveDelay;
    private bool _restoring;

    /// <summary>Index of the first visible row of the file list (updated by the page while scrolling).</summary>
    public int ScrollIndex { get; private set; }

    /// <summary>Raised after a restore so the page can scroll the list back to where it was.</summary>
    public event Action<int>? ScrollRequested;

    private string SessionStatePath => Path.Combine(_files.SessionDirectory, "state.json");

    private string EditsDirectory => Path.Combine(_files.SessionDirectory, "edits");

    private static string NewArchiveFileName() => $"archive-{Guid.NewGuid():N}.rpf";

    private string NewEditPath()
    {
        Directory.CreateDirectory(EditsDirectory);
        return Path.Combine(EditsDirectory, $"{Guid.NewGuid():N}.bin");
    }

    public void NotifyScrolled(int firstVisibleIndex)
    {
        ScrollIndex = Math.Max(0, firstVisibleIndex);
        SaveSessionSoon();
    }

    /// <summary>Called when the app goes to the background: Android may kill it there, so save now.</summary>
    public void OnBackgrounded()
    {
        _saveDelay?.Cancel();
        _ = SaveSessionNowAsync();
    }

    // ------------------------------------------------------------------ applying edits (used by Replace and by the viewer)

    /// <summary>
    /// Replaces <paramref name="node"/> with the bytes in <paramref name="editPath"/> (a file in the edits folder).
    /// The archive reads the file lazily and keeps it open, and the session remembers it for the next start.
    /// </summary>
    private void ApplyReplacementFile(Rpf6Archive archive, RpfNode node, string editPath)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(editPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18, FileOptions.Asynchronous);
            archive.ReplaceFile(node, stream, new RpfImportOptions());
            stream = null; // the archive owns it now
        }
        catch
        {
            stream?.Dispose();
            TryDelete(editPath);
            throw;
        }

        string key = node.GetHashPath();
        if (_edits.TryGetValue(key, out string? previous) && !string.Equals(previous, Path.GetFileName(editPath), StringComparison.Ordinal))
        {
            TryDelete(Path.Combine(EditsDirectory, previous)); // its stream was disposed by ReplaceFile
        }

        _edits[key] = Path.GetFileName(editPath);
        HasUnsavedChanges = true;
        SaveSessionSoon();
    }

    // ------------------------------------------------------------------ saving the session

    private async void SaveSessionSoon()
    {
        if (_archive == null || _restoring)
        {
            return;
        }

        _saveDelay?.Cancel();
        var cts = new CancellationTokenSource();
        _saveDelay = cts;
        try
        {
            await Task.Delay(600, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveSessionNowAsync();
    }

    private async Task SaveSessionNowAsync()
    {
        if (_archive == null || _sessionArchiveFile == null || _restoring)
        {
            return;
        }

        var state = new SessionState
        {
            DisplayName = SelectedRpfPath,
            ArchiveFile = _sessionArchiveFile,
            DirectoryPath = _currentDirectory?.GetHashPath() ?? "root",
            SearchText = SearchText,
            SelectedPath = SelectedNode is { IsParentLink: false } selected ? selected.Node.GetHashPath() : null,
            ScrollIndex = ScrollIndex,
        };

        foreach (KeyValuePair<string, string> edit in _edits)
        {
            state.Edits.Add(new SessionEdit { Path = edit.Key, File = edit.Value });
        }

        await _sessionLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_files.SessionDirectory);
            string temp = SessionStatePath + ".tmp";
            await File.WriteAllTextAsync(temp, SessionSerializer.Serialize(state));
            File.Move(temp, SessionStatePath, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort: failing to remember the screen position must never disturb the user.
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // ------------------------------------------------------------------ restoring the session

    private async Task RestoreSessionAsync()
    {
        if (!File.Exists(SessionStatePath))
        {
            PurgeSession(keepArchiveFile: null);   // no saved session: whatever is left in the folder is leftovers of an interrupted run
            return;
        }

        SessionState? state = null;
        try
        {
            state = SessionSerializer.Deserialize(await File.ReadAllTextAsync(SessionStatePath));
        }
        catch (Exception)
        {
            // handled below as "no usable session"
        }

        string? archivePath = state == null ? null : Path.Combine(_files.SessionDirectory, Path.GetFileName(state.ArchiveFile));
        if (state == null || archivePath == null || !File.Exists(archivePath))
        {
            PurgeSession(keepArchiveFile: null);
            return;
        }

        DeleteSessionOrphans(state, Path.GetFileName(archivePath));

        await RunAsync("Restoring your last session…", async ct =>
        {
            _restoring = true;
            try
            {
                Rpf6Archive archive = await Rpf6Archive.OpenFileAsync(archivePath, new RpfOpenOptions { Names = _names }, CoreProgress(), ct);
                SwapArchive(archive, archivePath);
                _sessionArchiveFile = Path.GetFileName(archivePath);
                SelectedRpfPath = state.DisplayName;

                _edits.Clear();
                int restored = 0;
                int skipped = 0;
                foreach (SessionEdit edit in state.Edits)
                {
                    string editPath = Path.Combine(EditsDirectory, Path.GetFileName(edit.File));
                    RpfNode? target = archive.FindByPath(edit.Path);
                    if (target == null || target.IsDirectory || !File.Exists(editPath))
                    {
                        skipped++;
                        TryDelete(editPath);
                        continue;
                    }

                    try
                    {
                        ApplyReplacementFile(archive, target, editPath);
                        restored++;
                    }
                    catch (Exception)
                    {
                        skipped++;
                    }
                }

                RpfNode directory = archive.FindByPath(state.DirectoryPath) ?? archive.Root;
                if (!directory.IsDirectory)
                {
                    directory = archive.Root;
                }

                RpfNode? selected = state.SelectedPath == null ? null : archive.FindByPath(state.SelectedPath);

                _suppressSearch = true;
                SearchText = state.SearchText;
                _suppressSearch = false;

                HasUnsavedChanges = restored > 0;
                ShowDirectory(directory, selected, keepSearch: true);
                RefreshNamesSummary();
                ScrollIndex = state.ScrollIndex;

                StatusMessage = $"Restored {SelectedRpfPath}"
                    + (restored > 0 ? $" with {restored:N0} unsaved edit(s)" : string.Empty)
                    + (skipped > 0 ? $" ({skipped:N0} edit(s) could not be restored)" : string.Empty) + ".";
            }
            finally
            {
                _restoring = false;
            }

            ScrollRequested?.Invoke(ScrollIndex);
            await SaveSessionNowAsync();
        });
    }

    // ------------------------------------------------------------------ closing / cleaning

    /// <summary>
    /// Deletes the session's files. With <paramref name="keepArchiveFile"/> the named working copy (and state.json)
    /// are kept and everything else goes; with <c>null</c> the whole session is removed.
    /// </summary>
    private void PurgeSession(string? keepArchiveFile)
    {
        _edits.Clear();
        try
        {
            if (Directory.Exists(EditsDirectory))
            {
                Directory.Delete(EditsDirectory, recursive: true);
            }

            foreach (string file in Directory.EnumerateFiles(_files.SessionDirectory))
            {
                string name = Path.GetFileName(file);
                if (keepArchiveFile != null && (name == keepArchiveFile || name == "state.json"))
                {
                    continue;
                }

                TryDelete(file);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Removes files in the session folder that the saved session does not use: working copies of earlier archives
    /// (left behind when the app was killed during a save) and edit files that no edit refers to.
    /// </summary>
    private void DeleteSessionOrphans(SessionState state, string keepArchiveFile)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_files.SessionDirectory))
            {
                string name = Path.GetFileName(file);
                if (name != keepArchiveFile && name != "state.json")
                {
                    TryDelete(file);
                }
            }

            if (Directory.Exists(EditsDirectory))
            {
                var used = new HashSet<string>(state.Edits.Select(e => Path.GetFileName(e.File)), StringComparer.Ordinal);
                foreach (string file in Directory.EnumerateFiles(EditsDirectory))
                {
                    if (!used.Contains(Path.GetFileName(file)))
                    {
                        TryDelete(file);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private bool CanCloseArchive() => !IsLoading && HasArchive;

    /// <summary>Closes the archive and deletes the saved session to free the storage it uses.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseArchive))]
    private async Task CloseArchiveAsync()
    {
        if (HasUnsavedChanges &&
            !await _dialogs.ConfirmAsync("Close without saving?", "This archive has edits that were not saved. They will be lost.", "Close", "Keep open"))
        {
            return;
        }

        _saveDelay?.Cancel();
        string? file = _workPath;
        _archive?.Dispose();
        _archive = null;
        TryDelete(file);
        _workPath = null;
        _sessionArchiveFile = null;
        PurgeSession(keepArchiveFile: null);

        _currentDirectory = null;
        ResetSelection();
        HasArchive = false;
        HasUnsavedChanges = false;
        FileTreeNodes = new ObservableCollection<RpfNodeItem>();
        SelectedNode = null;
        SelectedRpfPath = string.Empty;
        CurrentPath = string.Empty;
        ListSummary = string.Empty;
        ScrollIndex = 0;
        _suppressSearch = true;
        SearchText = string.Empty;
        _suppressSearch = false;
        RefreshNamesSummary();
        StatusMessage = "Archive closed and its saved copy deleted. Open an RPF archive to continue.";
    }
}
