using System.Collections.ObjectModel;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicRDR.App.Models;
using MagicRDR.App.Services;
using MagicRDR.Core;
using MagicRDR.Core.Formats;
using MagicRDR.Core.Session;

namespace MagicRDR.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IFileService _files;
    private readonly IDialogService _dialogs;
    private readonly RpfNameDictionary _names = new();

    private Rpf6Archive? _archive;
    private string? _workPath;            // local working copy the archive reads from
    private RpfNode? _currentDirectory;
    private CancellationTokenSource? _cts;
    private bool _initialized;

    public MainViewModel(IFileService files, IDialogService dialogs)
    {
        _files = files;
        _dialogs = dialogs;
    }

    // ------------------------------------------------------------------ bindable state

    /// <summary>Display name of the loaded archive (the picked document's name; Android gives no real path).</summary>
    [ObservableProperty]
    private string _selectedRpfPath = string.Empty;

    /// <summary>The rows of the folder currently shown ("..", first, then folders, then files).</summary>
    [ObservableProperty]
    private ObservableCollection<RpfNodeItem> _fileTreeNodes = new();

    [ObservableProperty]
    private RpfNodeItem? _selectedNode;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>0..1, bound straight to a ProgressBar.</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _statusMessage = "Open an RPF archive to get started.";

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private bool _hasArchive;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>Filter text for the current folder (matches names, case-insensitive).</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>"1,204 items" or "12 of 1,204 items" when a filter is active.</summary>
    [ObservableProperty]
    private string _listSummary = string.Empty;

    private bool _suppressSearch;

    /// <summary>"12,345 known names · 40 entries in this archive have no name".</summary>
    [ObservableProperty]
    private string _namesSummary = string.Empty;

    public string HeaderTitle => HasArchive ? SelectedRpfPath : "No archive open";

    public bool CanGoUp => !IsLoading && _currentDirectory?.Parent != null;

    public string EmptyTitle => IsLoading && !HasArchive ? "Loading…" : HasArchive ? "Nothing here" : "No archive open";

    public string EmptyHint => IsLoading && !HasArchive
        ? "Please wait."
        : !HasArchive
            ? "Tap Open RPF to browse a Red Dead Redemption archive."
            : SearchText.Trim().Length > 0 ? "No names match your filter." : "This folder is empty.";

    partial void OnIsLoadingChanged(bool value)
    {
        NotifyCommands();
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }

    partial void OnHasArchiveChanged(bool value)
    {
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        NotifyCommands();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyHint));
        if (_suppressSearch || _currentDirectory == null)
        {
            return;
        }

        ShowDirectory(_currentDirectory, SelectedNode?.Node, keepSearch: true);
    }

    partial void OnSelectedRpfPathChanged(string value) => OnPropertyChanged(nameof(HeaderTitle));

    partial void OnSelectedNodeChanged(RpfNodeItem? value)
    {
        if (TryToggleOnTap(value))
        {
            return;   // multi-select: the tap ticked (or unticked) the row
        }

        NotifyCommands();
        SaveSessionSoon();
    }

    private void NotifyCommands()
    {
        OpenRpfCommand.NotifyCanExecuteChanged();
        ExtractFileCommand.NotifyCanExecuteChanged();
        ReplaceFileCommand.NotifyCanExecuteChanged();
        SaveRpfCommand.NotifyCanExecuteChanged();
        LoadNamesCommand.NotifyCanExecuteChanged();
        FindNamesCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        CloseArchiveCommand.NotifyCanExecuteChanged();
        OpenViewerCommand.NotifyCanExecuteChanged();
        ApplyEditCommand.NotifyCanExecuteChanged();
        ToggleMultiSelectCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        SelectNoneCommand.NotifyCanExecuteChanged();
        NotifyTableCommands();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Called once when the page first appears: clears old scratch files and loads a bundled <c>names.txt</c> if present.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _files.CleanWorkDirectory();
        _files.CleanCache();

        int bundled = 0;
        int remembered = 0;

        // The names list is large, so it is read off the UI thread. The commands stay disabled until it is ready,
        // so no archive can be opened against a half-loaded list.
        IsLoading = true;
        StatusMessage = "Loading file names…";
        try
        {
            try
            {
                await using Stream? stream = await _files.OpenPackageFileAsync("names.txt");
                if (stream != null)
                {
                    bundled = await _names.LoadAsync(stream);
                }
            }
            catch (Exception)
            {
                // The bundled list is optional; unresolved entries simply show their hash.
            }

            // From here on only names the user adds are remembered in app storage; the bundled list never is.
            _names.MarkBaseline();

            try
            {
                if (File.Exists(UserNamesPath))
                {
                    await using var file = new FileStream(UserNamesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                    remembered = await _names.LoadAsync(file);
                }
            }
            catch (Exception)
            {
                // A damaged remembered-names file must never stop the app from starting.
            }
        }
        finally
        {
            IsLoading = false;
        }

        RefreshNamesSummary();
        StatusMessage = _names.Count > 0
            ? $"Ready. {_names.Count:N0} file names known ({bundled:N0} bundled, {remembered:N0} remembered)."
            : "Open an RPF archive to get started. No names list yet: use Import names after opening one.";

        await RestoreSessionAsync();
    }

    /// <summary>Releases the archive and scratch files. Called when the window is destroyed.</summary>
    public void Shutdown()
    {
        _cts?.Cancel();
        _archive?.Dispose();
        _archive = null;
        TryDelete(_workPath);
        _workPath = null;

        // The view model outlives the window; leave it in a clean "nothing open" state.
        _currentDirectory = null;
        HasArchive = false;
        HasUnsavedChanges = false;
        FileTreeNodes = new ObservableCollection<RpfNodeItem>();
        SelectedNode = null;
        SelectedRpfPath = string.Empty;
        CurrentPath = string.Empty;
        ListSummary = string.Empty;
        ResetSelection();
        _suppressSearch = true;
        SearchText = string.Empty;
        _suppressSearch = false;
        RefreshNamesSummary();
        StatusMessage = "Open an RPF archive to get started.";
    }

    // ------------------------------------------------------------------ commands

    private bool CanOpenRpf() => !IsLoading;

    private bool CanExtractFile() => !IsLoading && HasArchive
        && (IsMultiSelect ? SelectedCount > 0 : SelectedNode is { IsParentLink: false });

    private bool CanReplaceFile() => !IsLoading && HasArchive
        && (IsMultiSelect ? SelectedCount > 0 || FileTreeNodes.Any(i => i.IsFile) : SelectedNode is { IsFile: true });

    private bool CanSaveRpf() => !IsLoading && HasArchive;

    private bool CanLoadNames() => !IsLoading;

    private bool CanFindNames() => !IsLoading && HasArchive;

    private bool CanCancel() => IsLoading;

    /// <summary>Picks an .rpf, copies it to app storage, parses its header + TOC asynchronously and fills <see cref="FileTreeNodes"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenRpf))]
    private async Task OpenRpfAsync()
    {
        if (_archive?.HasPendingChanges == true &&
            !await _dialogs.ConfirmAsync("Discard changes?", "The open archive has edits that are not saved. Open another archive anyway?", "Discard", "Keep editing"))
        {
            return;
        }

        PickedFile? picked = await _files.PickAsync("Select an RPF archive");
        if (picked == null)
        {
            return;
        }

        await RunAsync($"Copying {picked.Name}…", async ct =>
        {
            string fileName = NewArchiveFileName();
            string local = Path.Combine(_files.SessionDirectory, fileName);
            await _files.CopyToAsync(picked, local, CopyProgress("Copying archive"), ct);

            Rpf6Archive archive;
            try
            {
                archive = await Rpf6Archive.OpenFileAsync(local, new RpfOpenOptions { Names = _names }, CoreProgress(), ct);
            }
            catch
            {
                TryDelete(local);
                throw;
            }

            if (archive.EmbeddedNamesFound > 0)
            {
                await SaveUserNamesAsync();
            }

            SwapArchive(archive, local);
            _sessionArchiveFile = fileName;
            PurgeSession(keepArchiveFile: fileName);   // forget the previous archive and its pending edits
            SelectedRpfPath = picked.Name;
            HasUnsavedChanges = false;
            ScrollIndex = 0;
            ShowDirectory(archive.Root);
            RefreshNamesSummary();
            StatusMessage = $"{archive.FileCount:N0} files in {archive.DirectoryCount:N0} folders · detected {archive.Platform}"
                + (archive.EmbeddedNamesFound > 0 ? $" · {archive.EmbeddedNamesFound:N0} names read from the archive" : string.Empty);
            await SaveSessionNowAsync();
        });
    }

    /// <summary>
    /// Extracts the selected file (decompressing it) or folder (as a .zip) and hands it to the system "save as" dialog.
    /// In multi-select mode every ticked file is extracted into one .zip instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExtractFile))]
    private async Task ExtractFileAsync()
    {
        if (IsMultiSelect)
        {
            await ExtractSelectionAsync();
            return;
        }

        Rpf6Archive? archive = _archive;
        RpfNode? node = SelectedNode?.Node;
        if (archive == null || node == null || SelectedNode!.IsParentLink)
        {
            return;
        }

        await RunAsync($"Extracting {node.Name}…", ct =>
            node.IsDirectory ? ExtractFolderAsync(archive, node, ct) : ExtractSingleFileAsync(archive, node, ct));
    }

    /// <summary>
    /// Replaces the selected file with one the user picks (translation injection). Compression is applied when the RPF is saved.
    /// In multi-select mode it replaces many entries at once from several picked files or from a ZIP.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReplaceFile))]
    private async Task ReplaceFileAsync()
    {
        if (IsMultiSelect)
        {
            await ReplaceBatchAsync();
            return;
        }

        Rpf6Archive? archive = _archive;
        RpfNode? node = SelectedNode?.Node;
        if (archive == null || node == null || node.IsDirectory)
        {
            return;
        }

        PickedFile? picked = await _files.PickAsync($"Choose the replacement for {node.Name}");
        if (picked == null)
        {
            return;
        }

        await RunAsync($"Reading {picked.Name}…", async ct =>
        {
            string editPath = NewEditPath();
            await _files.CopyToAsync(picked, editPath, CopyProgress("Reading replacement"), ct);
            ApplyReplacementFile(archive, node, editPath);

            ShowDirectory(_currentDirectory ?? archive.Root, node, keepSearch: true);

            string how = node.IsResource
                ? "stored as a packed resource"
                : node.IsCompressed ? "will be compressed when saved" : "will be stored uncompressed";
            StatusMessage = $"{node.Name} replaced with {picked.Name} ({how}). Press Save RPF to write it.";
        });
    }

    /// <summary>
    /// Rebuilds the archive (TOC, offsets, compression) into a new file, re-opens that file as the working archive,
    /// then offers it through the system "save as" dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveRpf))]
    private async Task SaveRpfAsync()
    {
        Rpf6Archive? archive = _archive;
        if (archive == null)
        {
            return;
        }

        await RunAsync("Saving archive…", async ct =>
        {
            string outName = NewArchiveFileName();
            string outPath = Path.Combine(_files.SessionDirectory, outName);
            string? returnTo = _currentDirectory?.GetHashPath();

            try
            {
                await archive.SaveToFileAsync(outPath, CoreProgress(), ct);
            }
            catch
            {
                TryDelete(outPath);
                throw;
            }

            // The saved file now has new offsets, so continue working from it (same as the original tool's reload).
            Rpf6Archive reopened;
            try
            {
                reopened = await Rpf6Archive.OpenFileAsync(outPath, new RpfOpenOptions { Names = _names }, null, ct);
            }
            catch
            {
                TryDelete(outPath);
                throw;
            }

            SwapArchive(reopened, outPath);
            _sessionArchiveFile = outName;
            PurgeSession(keepArchiveFile: outName);   // the edits are inside the new file now
            RefreshNamesSummary();
            HasUnsavedChanges = false;
            ShowDirectory(returnTo == null ? reopened.Root : reopened.FindByPath(returnTo) ?? reopened.Root, null, keepSearch: true);

            await SaveSessionNowAsync();
            StatusMessage = "Archive rebuilt. Choose where to save it…";
            ProgressValue = 1;

            await using var input = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ExportResult export = await _files.SaveAsync(SelectedRpfPath, input, ct);

            StatusMessage = export.Success
                ? $"Saved {SelectedRpfPath}."
                : $"Rebuilt, but not exported{(string.IsNullOrEmpty(export.Error) ? "" : ": " + export.Error)}. Press Save RPF to try again.";
        });
    }

    /// <summary>Loads a plain-text list of file names (one per line) so hashed entries show real names. The list is remembered.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadNames))]
    private async Task LoadNamesAsync()
    {
        PickedFile? picked = await _files.PickAsync("Choose a names list (one file name per line)");
        if (picked == null)
        {
            return;
        }

        await RunAsync("Loading names…", async ct =>
        {
            await using Stream stream = await picked.OpenReadAsync();
            int added = await _names.LoadAsync(stream, ct);
            bool saved = await SaveUserNamesAsync();
            AfterNamesChanged();

            StatusMessage = $"Loaded {added:N0} new names ({_names.Count:N0} known){(saved ? "" : " - could not be remembered for next time")}.";
        });
    }

    /// <summary>
    /// Reads the plain-text files inside the open archive and tries every word in them as a name.
    /// A name is kept only when its hash matches an entry that still has no name.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindNames))]
    private async Task FindNamesAsync()
    {
        Rpf6Archive? archive = _archive;
        if (archive == null)
        {
            return;
        }

        await RunAsync("Scanning text files for names…", async ct =>
        {
            RpfHarvestResult result = new(0, 0, 0);
            try
            {
                result = await RpfNameHarvester.HarvestAsync(archive, CoreProgress(), ct);
            }
            finally
            {
                // Also runs after Cancel, so names found before cancelling are kept.
                await SaveUserNamesAsync();
                AfterNamesChanged();
            }

            StatusMessage = result.Found > 0
                ? $"Found {result.Found:N0} new names. {result.Unresolved:N0} entries are still unnamed."
                : result.FilesScanned == 0
                    ? "No readable text files to scan in this archive."
                    : $"No new names found in {result.FilesScanned:N0} text files.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>The "Open" button on folder rows (and on the ".." row).</summary>
    [RelayCommand]
    private void OpenFolder(RpfNodeItem? item)
    {
        if (!IsLoading && item is { CanOpen: true })
        {
            ShowDirectory(item.Node);
        }
    }

    [RelayCommand]
    private void GoUp()
    {
        if (CanGoUp && _currentDirectory?.Parent is { } parent)
        {
            ShowDirectory(parent);
        }
    }

    // ------------------------------------------------------------------ extraction helpers

    private async Task ExtractSingleFileAsync(Rpf6Archive archive, RpfNode node, CancellationToken ct)
    {
        string temp = Path.Combine(_files.WorkDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 18, FileOptions.Asynchronous))
            {
                await archive.ExtractFileAsync(node, output, CoreProgress(), ct);
            }

            StatusMessage = "Choose where to save the file…";
            await using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ExportResult export = await _files.SaveAsync(Rpf6Archive.SanitizeSegment(node.Name), input, ct);

            StatusMessage = export.Success
                ? $"Saved {node.Name}."
                : $"Not saved{(string.IsNullOrEmpty(export.Error) ? "." : ": " + export.Error)}";
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task ExtractFolderAsync(Rpf6Archive archive, RpfNode directory, CancellationToken ct)
    {
        string tempDir = Path.Combine(_files.WorkDirectory, Guid.NewGuid().ToString("N"));
        string zipPath = tempDir + ".zip";
        try
        {
            int count = await archive.ExtractToDirectoryAsync(directory, tempDir, CoreProgress(), ct);
            if (count == 0)
            {
                StatusMessage = $"{directory.Name} has no files to extract.";
                return;
            }

            StatusMessage = $"Packing {count:N0} files into a zip…";
            ProgressValue = 1;
            await Task.Run(() => ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false), ct);

            StatusMessage = "Choose where to save the zip…";
            await using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ExportResult export = await _files.SaveAsync(Rpf6Archive.SanitizeSegment(directory.Name) + ".zip", input, ct);

            StatusMessage = export.Success
                ? $"Saved {count:N0} files from {directory.Name}."
                : $"Not saved{(string.IsNullOrEmpty(export.Error) ? "." : ": " + export.Error)}";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }

            TryDelete(zipPath);
        }
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Runs one long operation with the loading state, cancellation and error reporting wired up.</summary>
    private async Task RunAsync(string startMessage, Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        ProgressValue = 0;
        StatusMessage = startMessage;
        IsLoading = true;

        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
            await _dialogs.ShowMessageAsync("That didn't work", ex.Message);
        }
        finally
        {
            IsLoading = false;
            _cts.Dispose();
            _cts = null;
            _files.CleanCache();   // copies the system pickers made of the files that were just read
        }
    }

    private IProgress<RpfProgress> CoreProgress() => new Progress<RpfProgress>(p =>
    {
        if (!IsLoading)
        {
            return; // a late callback must not overwrite the final status message
        }

        ProgressValue = p.Fraction;
        StatusMessage = p.Message;
    });

    private IProgress<double> CopyProgress(string label) => new Progress<double>(fraction =>
    {
        if (!IsLoading)
        {
            return;
        }

        ProgressValue = fraction;
        StatusMessage = $"{label} {fraction:P0}";
    });

    private void SwapArchive(Rpf6Archive archive, string workPath)
    {
        Rpf6Archive? oldArchive = _archive;
        string? oldPath = _workPath;

        _archive = archive;
        _workPath = workPath;
        HasArchive = true;

        oldArchive?.Dispose();
        TryDelete(oldPath);
        ResetSelection();   // the ticked nodes belonged to the previous archive
    }

    private void ShowDirectory(RpfNode directory, RpfNode? select = null, bool keepSearch = false)
    {
        _currentDirectory = directory;

        if (!keepSearch && SearchText.Length > 0)
        {
            _suppressSearch = true;
            SearchText = string.Empty;
            _suppressSearch = false;
        }

        string filter = SearchText.Trim();
        IEnumerable<RpfNode> children = directory.Children;
        if (filter.Length > 0)
        {
            children = children.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var rows = new List<RpfNodeItem>(directory.Children.Count + 1);
        if (directory.Parent != null)
        {
            rows.Add(new RpfNodeItem(directory.Parent, isParentLink: true));
        }

        int shown = 0;
        foreach (RpfNode child in children
                     .OrderBy(c => c.IsDirectory ? 0 : 1)
                     .ThenBy(c => _names.Contains(c.NameHash) ? 0 : 1)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(CreateItem(child));
            shown++;
        }

        int total = directory.Children.Count;
        ListSummary = filter.Length > 0
            ? $"{shown:N0} of {total:N0} items"
            : total == 1 ? "1 item" : $"{total:N0} items";

        // One assignment (instead of Add() per row) so a folder with thousands of entries refreshes once.
        FileTreeNodes = new ObservableCollection<RpfNodeItem>(rows);
        _suppressSelectionToggle = true;
        SelectedNode = select == null ? null : rows.FirstOrDefault(r => !r.IsParentLink && ReferenceEquals(r.Node, select));
        _suppressSelectionToggle = false;
        CurrentPath = directory.GetPath();
        OnPropertyChanged(nameof(CanGoUp));
        SaveSessionSoon();
    }

    private string UserNamesPath => Path.Combine(_files.DataDirectory, "names.txt");

    /// <summary>Writes every known name to app storage so imported names survive a restart.</summary>
    private async Task<bool> SaveUserNamesAsync()
    {
        try
        {
            Directory.CreateDirectory(_files.DataDirectory);
            string temp = UserNamesPath + ".tmp";
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await _names.SaveAsync(file);
            }

            File.Move(temp, UserNamesPath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AfterNamesChanged()
    {
        if (_currentDirectory != null)
        {
            ShowDirectory(_currentDirectory, SelectedNode?.Node, keepSearch: true);
        }

        RefreshNamesSummary();
    }

    private void RefreshNamesSummary()
    {
        if (_archive == null)
        {
            NamesSummary = $"{_names.Count:N0} known names";
            return;
        }

        int unnamed = _archive.EnumerateAll().Count(n => !n.IsRoot && !_names.Contains(n.NameHash));
        NamesSummary = unnamed == 0
            ? $"{_names.Count:N0} known names · every entry here is named"
            : $"{_names.Count:N0} known names · {unnamed:N0} entries here have no name";
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
