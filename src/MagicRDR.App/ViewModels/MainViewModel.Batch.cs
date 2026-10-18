using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicRDR.App.Models;
using MagicRDR.App.Services;
using MagicRDR.Core;

namespace MagicRDR.App.ViewModels;

/// <summary>
/// Multi-select mode for the archive browser, plus the two batch operations that use it:
/// <list type="bullet">
/// <item><b>Extract</b>: every ticked file is extracted (progress, cancel) and handed over as one ZIP,
/// each file named by its resolved name or its hash (<c>0x00FEB5CC</c>).</item>
/// <item><b>Replace</b>: replacement files (several picked files, or the files of a ZIP) are matched to the ticked
/// entries - or to every file of the open folder when nothing is ticked - by name or hash name, with or without
/// extension. A summary is confirmed before anything changes.</item>
/// </list>
/// The ticked set is kept by node, so it survives changing folders and filtering. It is cleared when another archive is opened or the
/// archive is saved (saving rebuilds every node).
/// </summary>
public partial class MainViewModel
{
    private readonly HashSet<RpfNode> _selected = new();
    private bool _bulkSelecting;
    private bool _suppressSelectionToggle;

    [ObservableProperty]
    private bool _isMultiSelect;

    [ObservableProperty]
    private int _selectedCount;

    public string MultiSelectLabel => IsMultiSelect ? "Done selecting" : "Multi-select";

    public string SelectionSummary => SelectedCount == 1 ? "1 selected" : $"{SelectedCount:N0} selected";

    public string ExtractLabel => IsMultiSelect && SelectedCount > 0 ? $"Extract ({SelectedCount:N0})" : "Extract";

    public string ReplaceLabel => IsMultiSelect
        ? SelectedCount > 0 ? $"Replace ({SelectedCount:N0})" : "Replace many"
        : "Replace";

    partial void OnIsMultiSelectChanged(bool value)
    {
        foreach (RpfNodeItem item in FileTreeNodes)
        {
            item.ShowCheckBox = value && item.IsFile;
        }

        if (value)
        {
            SelectedNode = null;   // a tap now ticks a row instead of selecting it
        }

        OnPropertyChanged(nameof(MultiSelectLabel));
        UpdateSelectionState();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ExtractLabel));
        OnPropertyChanged(nameof(ReplaceLabel));
    }

    /// <summary>Builds one list row and gives it its tick state. Rows are rebuilt whenever a folder is shown.</summary>
    private RpfNodeItem CreateItem(RpfNode node)
    {
        var item = new RpfNodeItem(node);
        if (item.IsFile)
        {
            item.IsChecked = _selected.Contains(node);
            item.ShowCheckBox = IsMultiSelect;
            item.PropertyChanged += OnItemPropertyChanged;
        }

        return item;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkSelecting || e.PropertyName != nameof(RpfNodeItem.IsChecked) || sender is not RpfNodeItem item)
        {
            return;
        }

        if (item.IsChecked)
        {
            _selected.Add(item.Node);
        }
        else
        {
            _selected.Remove(item.Node);
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = _selected.Count;
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ExtractLabel));
        OnPropertyChanged(nameof(ReplaceLabel));
        NotifyCommands();
    }

    /// <summary>Forgets every tick (another archive was opened, the archive was saved or closed) and leaves multi-select mode.</summary>
    private void ResetSelection()
    {
        _selected.Clear();
        _bulkSelecting = true;
        foreach (RpfNodeItem item in FileTreeNodes)
        {
            item.IsChecked = false;
        }

        _bulkSelecting = false;
        IsMultiSelect = false;
        UpdateSelectionState();
    }

    private bool CanToggleMultiSelect() => !IsLoading && HasArchive;

    [RelayCommand(CanExecute = nameof(CanToggleMultiSelect))]
    private void ToggleMultiSelect() => IsMultiSelect = !IsMultiSelect;

    private bool CanSelectBulk() => !IsLoading && HasArchive && IsMultiSelect;

    /// <summary>Ticks every file in the folder that is shown (respecting the filter).</summary>
    [RelayCommand(CanExecute = nameof(CanSelectBulk))]
    private void SelectAll()
    {
        _bulkSelecting = true;
        foreach (RpfNodeItem item in FileTreeNodes)
        {
            if (item.IsFile)
            {
                item.IsChecked = true;
                _selected.Add(item.Node);
            }
        }

        _bulkSelecting = false;
        UpdateSelectionState();
    }

    /// <summary>Clears every tick, including the ones in other folders.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectBulk))]
    private void SelectNone()
    {
        _selected.Clear();
        _bulkSelecting = true;
        foreach (RpfNodeItem item in FileTreeNodes)
        {
            item.IsChecked = false;
        }

        _bulkSelecting = false;
        UpdateSelectionState();
    }

    /// <summary>In multi-select mode a tap on a file row ticks or unticks it. Returns <c>true</c> if the tap was used that way.</summary>
    private bool TryToggleOnTap(RpfNodeItem? item)
    {
        if (!IsMultiSelect || _suppressSelectionToggle || item == null)
        {
            return false;
        }

        if (item.IsFile)
        {
            item.IsChecked = !item.IsChecked;
        }

        // Clear the highlight so the same row can be tapped again.
        SelectedNode = null;
        return true;
    }

    /// <summary>The ticked files, in a stable order (by path).</summary>
    private List<RpfNode> GetSelectedNodes() =>
        _selected.Where(n => !n.IsDirectory).OrderBy(n => n.GetPath(), StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The files a batch Replace works on: the ticked ones, or - with nothing ticked - the files shown in this folder.</summary>
    private List<RpfNode> GetReplaceTargets(out bool fromSelection)
    {
        if (_selected.Count > 0)
        {
            fromSelection = true;
            return GetSelectedNodes();
        }

        fromSelection = false;
        return FileTreeNodes.Where(i => i.IsFile).Select(i => i.Node).ToList();
    }

    // ------------------------------------------------------------------ batch extract

    private async Task ExtractSelectionAsync()
    {
        Rpf6Archive? archive = _archive;
        if (archive == null)
        {
            return;
        }

        List<RpfNode> nodes = GetSelectedNodes();
        if (nodes.Count == 0)
        {
            StatusMessage = "Tick the files you want to extract first.";
            return;
        }

        if (nodes.Count == 1)
        {
            RpfNode only = nodes[0];
            await RunAsync($"Extracting {only.Name}…", ct => ExtractSingleFileAsync(archive, only, ct));
            return;
        }

        await RunAsync($"Extracting {nodes.Count:N0} files…", ct => ExtractNodesAsZipAsync(archive, nodes, ct));
    }

    private async Task ExtractNodesAsZipAsync(Rpf6Archive archive, List<RpfNode> nodes, CancellationToken ct)
    {
        string tempDir = Path.Combine(_files.WorkDirectory, Guid.NewGuid().ToString("N"));
        string zipPath = tempDir + ".zip";
        try
        {
            Directory.CreateDirectory(tempDir);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = nodes.Count;

            for (int index = 0; index < total; index++)
            {
                ct.ThrowIfCancellationRequested();
                RpfNode node = nodes[index];
                int position = index;
                string fileName = BatchNaming.UniqueName(Rpf6Archive.SanitizeSegment(node.Name), usedNames);

                var progress = new Progress<RpfProgress>(p =>
                {
                    if (!IsLoading)
                    {
                        return;
                    }

                    ProgressValue = (position + Math.Clamp(p.Fraction, 0d, 1d)) / total;
                    StatusMessage = $"Extracting {position + 1:N0} of {total:N0}: {node.Name}";
                });

                await using var output = new FileStream(Path.Combine(tempDir, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 18, FileOptions.Asynchronous);
                await archive.ExtractFileAsync(node, output, progress, ct);
            }

            StatusMessage = $"Packing {total:N0} files into a zip…";
            ProgressValue = 1;
            await Task.Run(() => ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false), ct);

            string zipName = Rpf6Archive.SanitizeSegment(Path.GetFileNameWithoutExtension(SelectedRpfPath)) + "-selection.zip";
            StatusMessage = "Choose where to save the zip…";
            await using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ExportResult export = await _files.SaveAsync(zipName, input, ct);

            StatusMessage = export.Success
                ? $"Saved {total:N0} files in {zipName}."
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

    // ------------------------------------------------------------------ batch replace

    /// <summary>One replacement file on offer: its name and a way to copy its bytes to a path.</summary>
    private sealed record BatchSource(string Name, Func<string, CancellationToken, Task> CopyToAsync);

    private async Task ReplaceBatchAsync()
    {
        Rpf6Archive? archive = _archive;
        if (archive == null)
        {
            return;
        }

        List<RpfNode> targets = GetReplaceTargets(out bool fromSelection);
        if (targets.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Nothing to replace", "There are no files here. Open a folder that has files, or tick some files first.");
            return;
        }

        string scope = fromSelection
            ? $"{targets.Count:N0} selected entries"
            : $"all {targets.Count:N0} files in this folder";
        bool pickFiles = await _dialogs.ConfirmAsync(
            "Replace many files",
            $"Replace {scope}. Files are matched by name or by hash name (0x00FEB5CC), with or without an extension.\n\nWhere are the new files?",
            "Pick files",
            "Pick a ZIP");

        IReadOnlyList<PickedFile> pickedFiles = Array.Empty<PickedFile>();
        PickedFile? pickedZip = null;
        if (pickFiles)
        {
            pickedFiles = await _files.PickManyAsync("Choose the replacement files");
            if (pickedFiles.Count == 0)
            {
                return;
            }
        }
        else
        {
            pickedZip = await _files.PickAsync("Choose a ZIP with the replacement files");
            if (pickedZip == null)
            {
                return;
            }
        }

        await RunAsync("Reading the replacement files…", async ct =>
        {
            string? zipPath = null;
            ZipArchive? zip = null;
            try
            {
                var sources = new List<BatchSource>();
                if (pickedZip != null)
                {
                    zipPath = Path.Combine(_files.WorkDirectory, Guid.NewGuid().ToString("N") + ".zip");
                    await _files.CopyToAsync(pickedZip, zipPath, CopyProgress("Reading ZIP"), ct);
                    zip = ZipFile.OpenRead(zipPath);
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (entry.Name.Length == 0)
                        {
                            continue;   // a folder
                        }

                        ZipArchiveEntry captured = entry;
                        sources.Add(new BatchSource(entry.Name, async (path, token) =>
                        {
                            await using Stream input = captured.Open();
                            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 18, FileOptions.Asynchronous);
                            await input.CopyToAsync(output, token);
                        }));
                    }
                }
                else
                {
                    foreach (PickedFile file in pickedFiles)
                    {
                        PickedFile captured = file;
                        sources.Add(new BatchSource(file.Name, (path, token) => _files.CopyToAsync(captured, path, null, token)));
                    }
                }

                BatchMatchResult match = BatchMatcher.Match(targets, sources.Select(s => s.Name).ToList());
                if (match.Matches.Count == 0)
                {
                    await _dialogs.ShowMessageAsync("No matching files", BuildMatchReport(match, targets.Count, sources, fromSelection));
                    StatusMessage = "No file matched an entry. Nothing was replaced.";
                    return;
                }

                bool go = await _dialogs.ConfirmAsync("Replace all?", BuildMatchReport(match, targets.Count, sources, fromSelection) + "\n\nReplace all?", "Replace all", "Cancel");
                if (!go)
                {
                    StatusMessage = "Batch replace cancelled. Nothing was changed.";
                    return;
                }

                int done = 0;
                var failures = new List<string>();
                try
                {
                    for (int i = 0; i < match.Matches.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        BatchMatch pair = match.Matches[i];
                        ProgressValue = (double)i / match.Matches.Count;
                        StatusMessage = $"Replacing {i + 1:N0} of {match.Matches.Count:N0}: {pair.Node.Name}";

                        string editPath = NewEditPath();
                        try
                        {
                            await sources[pair.SourceIndex].CopyToAsync(editPath, ct);
                            ApplyReplacementFile(archive, pair.Node, editPath);
                            done++;
                        }
                        catch (OperationCanceledException)
                        {
                            TryDelete(editPath);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            TryDelete(editPath);
                            failures.Add($"{pair.Node.Name}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    // Also after Cancel: whatever was replaced before it is already queued.
                    ShowDirectory(_currentDirectory ?? archive.Root, null, keepSearch: true);
                }

                ProgressValue = 1;
                StatusMessage = $"Replaced {done:N0} of {match.Matches.Count:N0} entries. Press Save RPF to write them.";
                if (failures.Count > 0)
                {
                    StatusMessage += $" {failures.Count:N0} failed.";
                    await _dialogs.ShowMessageAsync(
                        "Some files could not be used",
                        string.Join("\n", failures.Take(6)) + (failures.Count > 6 ? $"\n… and {failures.Count - 6:N0} more" : string.Empty));
                }
            }
            finally
            {
                zip?.Dispose();
                TryDelete(zipPath);
            }
        });
    }

    private static string BuildMatchReport(BatchMatchResult match, int targetCount, List<BatchSource> sources, bool fromSelection)
    {
        var text = new StringBuilder();
        text.Append($"Matched {match.Matches.Count:N0} of {targetCount:N0} {(fromSelection ? "selected entries" : "entries in this folder")}.");

        if (match.UnmatchedTargets.Count > 0)
        {
            text.Append($"\n{match.UnmatchedTargets.Count:N0} {(match.UnmatchedTargets.Count == 1 ? "entry has" : "entries have")} no file, for example {string.Join(", ", match.UnmatchedTargets.Take(3).Select(n => n.Name))}.");
        }

        if (match.AmbiguousTargets.Count > 0)
        {
            text.Append($"\n{match.AmbiguousTargets.Count:N0} {(match.AmbiguousTargets.Count == 1 ? "entry was" : "entries were")} skipped because more than one file fits, for example {string.Join(", ", match.AmbiguousTargets.Take(3).Select(n => n.Name))}.");
        }

        if (match.UnmatchedSources.Count > 0)
        {
            text.Append($"\n{match.UnmatchedSources.Count:N0} {(match.UnmatchedSources.Count == 1 ? "file matches" : "files match")} no entry, for example {string.Join(", ", match.UnmatchedSources.Take(3).Select(i => sources[i].Name))}.");
        }

        return text.ToString();
    }
}
