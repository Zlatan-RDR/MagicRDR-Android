using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicRDR.App.Models;
using MagicRDR.App.Services;
using MagicRDR.Core;
using MagicRDR.Core.Formats;

namespace MagicRDR.App.ViewModels;

public enum TableKind
{
    None,
    Hash,
    Languages,
}

/// <summary>What the viewer found when it tried to read a file as a string table.</summary>
public sealed record TableLoad(TableKind Kind, StringTableData? Hash, StrtblDocument? Strtbl, bool Editable, string Note)
{
    public static TableLoad None(string note) => new(TableKind.None, null, null, false, note);
}

/// <summary>
/// The string-table viewer / editor (the "Table" pane of the viewer). Two formats are understood:
/// hash tables (resource type 1, the <c>.wst</c> files) and multi-language tables (the <c>.strtbl</c> files).
/// Edits are kept in the parsed document; <b>Apply table to archive</b> writes the whole table back
/// and queues it as the new content of the file (Save RPF then writes the archive).
/// </summary>
public partial class MainViewModel
{
    private const long MaxTableBytes = 32L * 1024 * 1024;

    private TableLoad _tableLoad = TableLoad.None(string.Empty);
    private List<TableRowItem> _allRows = new();
    private string[]? _baselineHash;       // texts as last applied to the archive, hash table
    private string[][]? _baselineLanguages; // the same, one array per language

    [ObservableProperty]
    private ObservableCollection<TableRowItem> _tableRows = new();

    [ObservableProperty]
    private TableRowItem? _selectedTableRow;

    [ObservableProperty]
    private string _tableFilter = string.Empty;

    [ObservableProperty]
    private string _tableSummary = string.Empty;

    [ObservableProperty]
    private string _rowEditorText = string.Empty;

    [ObservableProperty]
    private bool _hasLanguages;

    [ObservableProperty]
    private List<string> _languageChoices = new();

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private bool _tableEditable;

    [ObservableProperty]
    private bool _hasTableEdits;

    /// <summary>
    /// "Arabic Visual Fix". The game stores Arabic as shaped Presentation Forms laid out left to right (visual order), which
    /// Android would reverse a second time. When this is on the list, the filter, the entry editor and the exported text show
    /// natural Arabic, and what you type is converted back to the game's form when it is stored (Apply entry, Apply table, Import).
    /// </summary>
    [ObservableProperty]
    private bool _arabicVisualFix = true;

    private bool _suppressArabicRebuild;

    public bool HasRowSelected => SelectedTableRow != null;

    public bool IsTableReadOnly => !TableEditable;

    partial void OnSelectedTableRowChanged(TableRowItem? value)
    {
        RowEditorText = value?.Value ?? string.Empty;
        OnPropertyChanged(nameof(HasRowSelected));
        NotifyTableCommands();
    }

    partial void OnTableEditableChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTableReadOnly));
        NotifyTableCommands();
    }

    partial void OnHasTableEditsChanged(bool value) => NotifyTableCommands();

    partial void OnTableFilterChanged(string value) => RefreshTableRows();

    partial void OnArabicVisualFixChanged(bool value)
    {
        if (!_suppressArabicRebuild && _tableLoad.Kind != TableKind.None)
        {
            BuildRows();   // show every text again in the other form
            StatusMessage = value
                ? "Arabic Visual Fix on: Arabic is shown in natural reading order."
                : "Arabic Visual Fix off: texts are shown exactly as stored.";
        }
    }

    /// <summary>Stored text to what the list and editor show.</summary>
    private string ToDisplay(string? stored) => ArabicVisualFix ? ArabicVisualCodec.Decode(stored) : stored ?? string.Empty;

    /// <summary>What the list or editor shows back to the form that is stored in the table.</summary>
    private string ToStored(string? display) => ArabicVisualFix ? ArabicVisualCodec.Encode(display) : display ?? string.Empty;

    /// <summary>The text stored at <paramref name="index"/> (hash table or the selected language).</summary>
    private string StoredAt(int index)
    {
        if (_tableLoad.Kind == TableKind.Hash && _tableLoad.Hash is { } hash && index >= 0 && index < hash.Entries.Count)
        {
            return hash.Entries[index].Value ?? string.Empty;
        }

        if (_tableLoad.Kind == TableKind.Languages && _tableLoad.Strtbl is { } doc
            && SelectedLanguageIndex >= 0 && SelectedLanguageIndex < doc.Segments.Count)
        {
            List<string> strings = doc.Segments[SelectedLanguageIndex].Strings;
            if (index >= 0 && index < strings.Count)
            {
                return strings[index] ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>The text at <paramref name="index"/> as it was when the table was loaded or last applied to the archive.</summary>
    private string? BaselineStoredAt(int index)
    {
        if (_tableLoad.Kind == TableKind.Hash && _baselineHash != null && index >= 0 && index < _baselineHash.Length)
        {
            return _baselineHash[index];
        }

        if (_tableLoad.Kind == TableKind.Languages && _baselineLanguages != null
            && SelectedLanguageIndex >= 0 && SelectedLanguageIndex < _baselineLanguages.Length)
        {
            string[] language = _baselineLanguages[SelectedLanguageIndex];
            if (index >= 0 && index < language.Length)
            {
                return language[index];
            }
        }

        return null;
    }

    /// <summary>True if any text in the table is already stored in the game's Arabic form.</summary>
    private static bool StoresVisualArabic(TableLoad load)
    {
        if (load.Hash != null)
        {
            foreach (StringTableEntry entry in load.Hash.Entries)
            {
                if (ArabicVisualCodec.HasPresentationForms(entry.Value))
                {
                    return true;
                }
            }
        }

        if (load.Strtbl != null)
        {
            foreach (StrtblSegment segment in load.Strtbl.Segments)
            {
                foreach (string? text in segment.Strings)
                {
                    if (ArabicVisualCodec.HasPresentationForms(text))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_tableLoad.Kind == TableKind.Languages)
        {
            BuildRows();
        }
    }

    private void NotifyTableCommands()
    {
        ApplyRowCommand.NotifyCanExecuteChanged();
        RevertRowCommand.NotifyCanExecuteChanged();
        ApplyTableCommand.NotifyCanExecuteChanged();
        ExportTableCommand.NotifyCanExecuteChanged();
        ImportTableCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ loading

    private async Task<TableLoad> TryLoadTableAsync(RpfNode node, byte[] data, RpfPlatform platform, CancellationToken ct)
    {
        if (node.IsResource)
        {
            string detail = RpfResourceUnpacker.Describe(data, platform);
            string suffix = detail.Length == 0 ? string.Empty : $" [{detail}]";
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                RpfResourceInfo info = RpfResourceInfo.Read(stream, platform);
                if (!info.IsResource)
                {
                    return TableLoad.None(string.Empty);
                }

                if (info.ResourceType != 1)
                {
                    return TableLoad.None($"Packed resource, type {info.ResourceType}. Only string tables (type 1) can be opened so far.{suffix}");
                }

                RpfUnpackResult unpacked = await RpfResourceUnpacker.UnpackDetailedAsync(data, platform, ct);
                StringTableData table = StringTableReader.ReadDocument(unpacked.Data, littleEndian: platform == RpfPlatform.Switch);
                string repaired = unpacked.OpenEndedFrames > 0 ? " The compressed data had no end marker and was repaired before reading." : string.Empty;
                bool editable = platform == RpfPlatform.Switch;
                string note = $"String table with {table.Entries.Count:N0} entries.{repaired}"
                    + (editable ? " Edit the text, then Apply table to archive." : " Read-only on this platform.");
                return new TableLoad(TableKind.Hash, table, null, editable, note);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return TableLoad.None($"Could not read this resource as a string table: {ex.Message}{suffix}");
            }
        }

        if (data.Length > 0 && data.Length <= MaxTableBytes && StrtblCodec.TryRead(data, out StrtblDocument? document, out _) && document != null)
        {
            bool exact = StrtblCodec.RoundTripsExactly(data, document);
            string note = exact
                ? $"Language table with {document.Segments.Count} languages. Pick a language, edit the text, then Apply table to archive."
                : $"Language table with {document.Segments.Count} languages, shown read-only: writing it back would not reproduce the original bytes exactly, so editing is disabled to protect your data.";
            return new TableLoad(TableKind.Languages, null, document, exact, note);
        }

        return TableLoad.None(string.Empty);
    }

    private void InitializeTable(TableLoad load)
    {
        if (StoresVisualArabic(load) && !ArabicVisualFix)
        {
            _suppressArabicRebuild = true;   // the rows are built below, once
            ArabicVisualFix = true;
            _suppressArabicRebuild = false;
        }

        _tableLoad = load;
        TableEditable = load.Editable;
        TableFilter = string.Empty;
        SelectedTableRow = null;
        SnapshotBaseline();

        if (load.Kind == TableKind.Languages && load.Strtbl != null)
        {
            HasLanguages = true;
            LanguageChoices = load.Strtbl.Segments
                .Select((segment, i) => StrtblCodec.IsUnicode(i) ? segment.Language : $"{segment.Language} (single-byte)")
                .ToList();
            SelectedLanguageIndex = 0;
        }
        else
        {
            HasLanguages = false;
            LanguageChoices = new List<string>();
            SelectedLanguageIndex = 0;
        }

        BuildRows();
    }

    private void SnapshotBaseline()
    {
        _baselineHash = _tableLoad.Hash?.Entries.Select(e => e.Value ?? string.Empty).ToArray();
        _baselineLanguages = _tableLoad.Strtbl?.Segments.Select(s => s.Strings.Select(t => t ?? string.Empty).ToArray()).ToArray();
    }

    private void ClearTable()
    {
        _tableLoad = TableLoad.None(string.Empty);
        _baselineHash = null;
        _baselineLanguages = null;
        _allRows = new List<TableRowItem>();
        TableRows = new ObservableCollection<TableRowItem>();
        SelectedTableRow = null;
        TableFilter = string.Empty;
        TableSummary = string.Empty;
        RowEditorText = string.Empty;
        HasLanguages = false;
        HasTableEdits = false;
        TableEditable = false;
    }

    private void BuildRows()
    {
        var rows = new List<TableRowItem>();
        switch (_tableLoad.Kind)
        {
            case TableKind.Hash when _tableLoad.Hash != null:
                IReadOnlyList<StringTableEntry> entries = _tableLoad.Hash.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    string baseline = _baselineHash != null && i < _baselineHash.Length ? _baselineHash[i] : entries[i].Value ?? string.Empty;
                    rows.Add(new TableRowItem(i, $"0x{entries[i].Key:X8}", ToDisplay(entries[i].Value), ToDisplay(baseline)));
                }

                break;

            case TableKind.Languages when _tableLoad.Strtbl != null:
                int language = Math.Clamp(SelectedLanguageIndex, 0, Math.Max(0, _tableLoad.Strtbl.Segments.Count - 1));
                List<string> strings = _tableLoad.Strtbl.Segments[language].Strings;
                for (int i = 0; i < strings.Count; i++)
                {
                    string[]? baselineStrings = _baselineLanguages != null && language < _baselineLanguages.Length ? _baselineLanguages[language] : null;
                    string baseline = baselineStrings != null && i < baselineStrings.Length ? baselineStrings[i] : strings[i] ?? string.Empty;
                    rows.Add(new TableRowItem(i, string.Empty, ToDisplay(strings[i]), ToDisplay(baseline)));
                }

                break;
        }

        _allRows = rows;
        SelectedTableRow = null;
        RefreshTableRows();
    }

    private void RefreshTableRows()
    {
        string filter = TableFilter.Trim();
        IEnumerable<TableRowItem> view = _allRows;
        if (filter.Length > 0)
        {
            view = _allRows.Where(r =>
                r.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.KeyText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Index.ToString().Equals(filter, StringComparison.Ordinal));
        }

        TableRows = new ObservableCollection<TableRowItem>(view);
        UpdateTableSummary();
    }

    private void UpdateTableSummary()
    {
        int edited = _allRows.Count(r => r.IsEdited);
        HasTableEdits = edited > 0;
        TableSummary = TableFilter.Trim().Length > 0
            ? $"{TableRows.Count:N0} of {_allRows.Count:N0} entries · {edited:N0} edited"
            : $"{_allRows.Count:N0} entries · {edited:N0} edited";
    }

    // ------------------------------------------------------------------ editing one entry

    private bool CanApplyRow() => !IsLoading && TableEditable && SelectedTableRow != null;

    /// <summary>Stores the text from the entry editor in the table (not in the archive yet).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRow))]
    private async Task ApplyRowAsync()
    {
        TableRowItem? row = SelectedTableRow;
        if (row == null)
        {
            return;
        }

        string value = RowEditorText ?? string.Empty;
        string? problem = TrySetValue(row.Index, value);
        if (problem != null)
        {
            await _dialogs.ShowMessageAsync("This text cannot be stored", problem);
            return;
        }

        row.Value = ToDisplay(StoredAt(row.Index));   // what the table now holds, shown the way the list shows it
        RowEditorText = row.Value;
        UpdateTableSummary();
        StatusMessage = $"Entry {row.Index} changed. Apply table to archive when you are done.";
    }

    private bool CanRevertRow() => !IsLoading && SelectedTableRow is { IsEdited: true };

    /// <summary>Puts the original text back in the editor (press Apply entry to store it).</summary>
    [RelayCommand(CanExecute = nameof(CanRevertRow))]
    private void RevertRow()
    {
        if (SelectedTableRow is { } row)
        {
            RowEditorText = row.Original;
        }
    }

    /// <summary>Writes one text into the parsed document. Returns a message when it cannot be stored, otherwise <c>null</c>.</summary>
    private string? TrySetValue(int index, string value)
    {
        value ??= string.Empty;
        if (_tableLoad.Kind == TableKind.Hash && _tableLoad.Hash is { } hash)
        {
            if (index < 0 || index >= hash.Entries.Count)
            {
                return $"There is no entry {index}.";
            }

            // Text that reads the same as the stored one is left exactly as it is, so editing one line
            // (or importing a file) never rewrites the others, and reverting puts the original bytes back.
            if (ArabicVisualFix)
            {
                string? original = BaselineStoredAt(index);
                if (original != null && string.Equals(ToDisplay(original), value, StringComparison.Ordinal))
                {
                    hash.Entries[index] = hash.Entries[index] with { Value = original };
                    return null;
                }

                if (string.Equals(ToDisplay(hash.Entries[index].Value), value, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            hash.Entries[index] = hash.Entries[index] with { Value = ToStored(value) };
            return null;
        }

        if (_tableLoad.Kind == TableKind.Languages && _tableLoad.Strtbl is { } doc)
        {
            int language = SelectedLanguageIndex;
            List<string> strings = doc.Segments[language].Strings;
            if (index < 0 || index >= strings.Count)
            {
                return $"There is no entry {index}.";
            }

            if (ArabicVisualFix)
            {
                string? original = BaselineStoredAt(index);
                if (original != null && string.Equals(ToDisplay(original), value, StringComparison.Ordinal))
                {
                    strings[index] = original;
                    return null;
                }

                if (string.Equals(ToDisplay(strings[index]), value, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            string stored = ToStored(value);
            if (!StrtblCodec.IsUnicode(language))
            {
                foreach (char c in stored)
                {
                    if (c > 0xFF)
                    {
                        return $"The character U+{(int)c:X4} cannot be stored in {doc.Segments[language].Language}, which keeps one byte per character. Use a language slot that stores Unicode (German, Italian, Polish, Russian…).";
                    }
                }
            }

            strings[index] = stored;
            return null;
        }

        return "No table is open.";
    }

    // ------------------------------------------------------------------ applying to the archive

    private bool CanApplyTable() => !IsLoading && IsViewerOpen && IsTableMode && TableEditable && HasTableEdits;

    /// <summary>Writes the whole table (every language, or the hash table) and queues it as the file's new content.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyTable))]
    private async Task ApplyTableAsync()
    {
        Rpf6Archive? archive = _archive;
        RpfNode? node = _viewerNode;
        TableLoad load = _tableLoad;
        if (archive == null || node == null || load.Kind == TableKind.None)
        {
            return;
        }

        if (load.Strtbl != null && StrtblCodec.Validate(load.Strtbl) is { } problem)
        {
            await _dialogs.ShowMessageAsync("This table cannot be written", problem);
            return;
        }

        await RunAsync($"Writing {node.Name}…", async ct =>
        {
            byte[] bytes = load.Kind == TableKind.Hash
                ? await StringTableWriter.PackAsync(load.Hash!, archive.Platform, ct)
                : await Task.Run(() => StrtblCodec.Write(load.Strtbl!), ct);

            string editPath = NewEditPath();
            await File.WriteAllBytesAsync(editPath, bytes, ct);
            ApplyReplacementFile(archive, node, editPath);

            SnapshotBaseline();
            BuildRows();

            _viewerData = bytes;
            _hexPage = 0;
            UpdateHex();
            ViewerInfo = $"{SizeFormatter.Format(bytes.Length)} · edited · {RpfHash.FormatHexHash(node.NameHash)}";
            UpdateTableSummary();
            ShowDirectory(_currentDirectory ?? archive.Root, SelectedNode?.Node, keepSearch: true);
            StatusMessage = $"{node.Name} updated in the archive ({SizeFormatter.Format(bytes.Length)}). Press Save RPF to write it.";
        });
    }

    // ------------------------------------------------------------------ export / import

    private bool CanUseTableFiles() => !IsLoading && IsViewerOpen && IsTableMode && _tableLoad.Kind != TableKind.None;

    /// <summary>Saves the current texts as a plain text file for translating elsewhere.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTableFiles))]
    private async Task ExportTableAsync()
    {
        TableLoad load = _tableLoad;
        string baseName = Rpf6Archive.SanitizeSegment(ViewerTitle);
        string text;
        string fileName;
        if (load.Kind == TableKind.Hash && load.Hash != null)
        {
            List<StringTableEntry> shown = load.Hash.Entries.Select(e => e with { Value = ToDisplay(e.Value) }).ToList();
            text = StringTableText.ExportHashTable(shown);
            fileName = baseName + ".txt";
        }
        else if (load.Strtbl != null)
        {
            StrtblSegment segment = load.Strtbl.Segments[SelectedLanguageIndex];
            text = StringTableText.ExportLanguage(segment.Language, segment.Strings.Select(s => ToDisplay(s)).ToList());
            fileName = $"{baseName}-{Rpf6Archive.SanitizeSegment(segment.Language)}.txt";
        }
        else
        {
            return;
        }

        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        await RunAsync("Exporting…", async ct =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            ExportResult result = await _files.SaveAsync(fileName, stream, ct);
            StatusMessage = result.Success
                ? $"Exported {_allRows.Count:N0} entries to {fileName}."
                : $"Not exported{(string.IsNullOrEmpty(result.Error) ? "." : ": " + result.Error)}";
        });
    }

    /// <summary>Reads a translated text file (the export format) and stores every matching line in the table.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTableFiles))]
    private async Task ImportTableAsync()
    {
        if (!TableEditable)
        {
            await _dialogs.ShowMessageAsync("Read-only table", "This table is shown read-only, so it cannot be changed by an import.");
            return;
        }

        PickedFile? picked = await _files.PickAsync("Choose the translated text file");
        if (picked == null)
        {
            return;
        }

        await RunAsync($"Importing {picked.Name}…", async ct =>
        {
            string text;
            await using (Stream stream = await picked.OpenReadAsync())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = await reader.ReadToEndAsync(ct);
            }

            var rejected = new List<string>();
            List<StringTableTextLine> lines = StringTableText.Parse(text.Split('\n'), rejected);
            int applied = 0;
            foreach (StringTableTextLine line in lines)
            {
                if (_tableLoad.Kind == TableKind.Hash && _tableLoad.Hash is { } hash
                    && line.Key is { } key && line.Index >= 0 && line.Index < hash.Entries.Count && hash.Entries[line.Index].Key != key)
                {
                    rejected.Add($"line {line.LineNumber}: the key 0x{key:X8} does not match entry {line.Index}");
                    continue;
                }

                string? problem = TrySetValue(line.Index, line.Value);
                if (problem != null)
                {
                    rejected.Add($"line {line.LineNumber}: {problem}");
                    continue;
                }

                applied++;
            }

            BuildRows();
            string report = $"Imported {applied:N0} of {lines.Count:N0} lines from {picked.Name}.";
            if (rejected.Count > 0)
            {
                report += $" {rejected.Count:N0} skipped, for example: {rejected[0]}.";
            }

            ViewerNotice = report + " Apply table to archive to keep the changes.";
            StatusMessage = report;
        });
    }
}
