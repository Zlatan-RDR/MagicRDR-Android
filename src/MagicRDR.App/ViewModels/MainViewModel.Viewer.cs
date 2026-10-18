using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagicRDR.App.Models;
using MagicRDR.Core;
using MagicRDR.Core.Formats;

namespace MagicRDR.App.ViewModels;

public enum ViewerPane
{
    Text,
    Hex,
    Table,
}

/// <summary>
/// The in-app file viewer / editor (opened over the file list):
/// <list type="bullet">
/// <item><b>Text</b>: plain text files (XML, txt, csv…) can be edited. "Apply to archive" queues the change; Save RPF writes it.</item>
/// <item><b>Hex</b>: read-only, 4 KB per page, for any file.</item>
/// <item><b>Table</b>: string tables (hash tables and multi-language tables), see <c>MainViewModel.Table.cs</c>.</item>
/// </list>
/// </summary>
public partial class MainViewModel
{
    private const long MaxViewBytes = 32L * 1024 * 1024;
    private const int MaxEditBytes = 512 * 1024;
    private const int HexPageSize = 4096;

    private RpfNode? _viewerNode;
    private byte[]? _viewerData;
    private TextDocument? _textDocument;
    private string _originalText = string.Empty;
    private int _hexPage;
    private bool _loadingEditor;

    [ObservableProperty]
    private bool _isViewerOpen;

    [ObservableProperty]
    private string _viewerTitle = string.Empty;

    [ObservableProperty]
    private string _viewerInfo = string.Empty;

    [ObservableProperty]
    private string _viewerNotice = string.Empty;

    [ObservableProperty]
    private ViewerPane _viewerMode = ViewerPane.Hex;

    [ObservableProperty]
    private string _editorText = string.Empty;

    [ObservableProperty]
    private bool _isEditorDirty;

    [ObservableProperty]
    private bool _isTextAvailable;

    [ObservableProperty]
    private bool _isTableAvailable;

    [ObservableProperty]
    private bool _canEditText;

    [ObservableProperty]
    private string _hexText = string.Empty;

    [ObservableProperty]
    private string _hexPageLabel = string.Empty;

    [ObservableProperty]
    private string _textInfo = string.Empty;

    public bool IsTextMode => ViewerMode == ViewerPane.Text;

    public bool IsHexMode => ViewerMode == ViewerPane.Hex;

    public bool IsTableMode => ViewerMode == ViewerPane.Table;

    public bool IsEditorReadOnly => !CanEditText;

    partial void OnViewerModeChanged(ViewerPane value)
    {
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsHexMode));
        OnPropertyChanged(nameof(IsTableMode));
        ApplyEditCommand.NotifyCanExecuteChanged();
        NotifyTableCommands();
    }

    partial void OnCanEditTextChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditorReadOnly));
        ApplyEditCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEditorDirtyChanged(bool value) => ApplyEditCommand.NotifyCanExecuteChanged();

    partial void OnEditorTextChanged(string value)
    {
        if (_loadingEditor || _textDocument == null)
        {
            return;
        }

        IsEditorDirty = !string.Equals(value, _originalText, StringComparison.Ordinal);
        TextInfo = $"{TextCodec.Describe(_textDocument.Format)} · {value.Length:N0} characters";
    }

    // ------------------------------------------------------------------ opening

    private bool CanOpenViewer(RpfNodeItem? item) => !IsLoading && HasArchive && (item ?? SelectedNode) is { IsFile: true };

    /// <summary>Opens the viewer for the given row (the "View" button on a row) or for the selected file (bottom bar).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenViewer))]
    private async Task OpenViewerAsync(RpfNodeItem? item)
    {
        Rpf6Archive? archive = _archive;
        RpfNode? node = (item ?? SelectedNode)?.Node;
        if (archive == null || node == null || node.IsDirectory)
        {
            return;
        }

        if (node.UncompressedSize > MaxViewBytes)
        {
            await _dialogs.ShowMessageAsync("File too large", $"{node.Name} is {SizeFormatter.Format(node.UncompressedSize)}. The viewer opens files up to {SizeFormatter.Format(MaxViewBytes)}; use Extract for bigger ones.");
            return;
        }

        await RunAsync($"Opening {node.Name}…", async ct =>
        {
            using var buffer = new MemoryStream((int)Math.Min(node.UncompressedSize, int.MaxValue));
            await archive.ExtractFileAsync(node, buffer, CoreProgress(), ct);
            byte[] data = buffer.ToArray();

            TableLoad table = await TryLoadTableAsync(node, data, archive.Platform, ct);
            PrepareViewer(node, data, table);
        });
    }

    private void PrepareViewer(RpfNode node, byte[] data, TableLoad table)
    {
        _viewerNode = node;
        _viewerData = data;

        string kind = node.IsResource ? "packed resource" : node.IsCompressed ? "compressed" : "stored";
        ViewerTitle = node.Name;
        ViewerInfo = $"{SizeFormatter.Format(data.Length)} · {kind} · {RpfHash.FormatHexHash(node.NameHash)}";

        // text
        _textDocument = null;
        _originalText = string.Empty;
        bool textAvailable = table.Kind == TableKind.None && !node.IsResource && data.Length > 0 && data.Length <= MaxEditBytes && TextCodec.LooksLikeText(data);
        _loadingEditor = true;
        if (textAvailable)
        {
            _textDocument = TextCodec.Decode(data);
            _originalText = _textDocument.Text;
            EditorText = _originalText;
            TextInfo = $"{TextCodec.Describe(_textDocument.Format)} · {_originalText.Length:N0} characters";
        }
        else
        {
            EditorText = string.Empty;
            TextInfo = string.Empty;
        }

        _loadingEditor = false;
        IsEditorDirty = false;
        IsTextAvailable = textAvailable;
        CanEditText = textAvailable;

        // table
        IsTableAvailable = table.Kind != TableKind.None;
        if (IsTableAvailable)
        {
            InitializeTable(table);
        }
        else
        {
            ClearTable();
        }

        // hex
        _hexPage = 0;
        UpdateHex();

        // notice + first mode
        if (IsTableAvailable)
        {
            ViewerNotice = table.Note;
            ViewerMode = ViewerPane.Table;
        }
        else if (textAvailable)
        {
            ViewerNotice = "Plain text: you can edit it. Apply to archive, then Save RPF.";
            ViewerMode = ViewerPane.Text;
        }
        else
        {
            ViewerNotice = !string.IsNullOrEmpty(table.Note)
                ? table.Note
                : node.IsResource
                    ? "Packed resource: hex view only."
                    : data.Length > MaxEditBytes && TextCodec.LooksLikeText(data)
                        ? $"Text larger than {SizeFormatter.Format(MaxEditBytes)} is shown as hex only; Extract it to edit elsewhere."
                        : "Binary file: hex view only.";
            ViewerMode = ViewerPane.Hex;
        }

        IsViewerOpen = true;
        StatusMessage = $"Opened {node.Name}.";
    }

    // ------------------------------------------------------------------ modes and hex paging

    [RelayCommand]
    private void ShowText()
    {
        if (IsTextAvailable)
        {
            ViewerMode = ViewerPane.Text;
        }
    }

    [RelayCommand]
    private void ShowHex() => ViewerMode = ViewerPane.Hex;

    [RelayCommand]
    private void ShowTable()
    {
        if (IsTableAvailable)
        {
            ViewerMode = ViewerPane.Table;
        }
    }

    [RelayCommand]
    private void HexPrevious()
    {
        if (_hexPage > 0)
        {
            _hexPage--;
            UpdateHex();
        }
    }

    [RelayCommand]
    private void HexNext()
    {
        if (_viewerData != null && (_hexPage + 1) * HexPageSize < _viewerData.Length)
        {
            _hexPage++;
            UpdateHex();
        }
    }

    private void UpdateHex()
    {
        byte[] data = _viewerData ?? Array.Empty<byte>();
        int pages = Math.Max(1, (data.Length + HexPageSize - 1) / HexPageSize);
        int start = Math.Min(_hexPage * HexPageSize, data.Length);
        int length = Math.Min(HexPageSize, data.Length - start);

        HexText = HexDumper.Format(data.AsSpan(start, length), start, bytesPerRow: 8);
        HexPageLabel = $"Page {_hexPage + 1} / {pages}";
    }

    // ------------------------------------------------------------------ applying text and closing

    private bool CanApplyEdit() => !IsLoading && IsViewerOpen && IsTextMode && CanEditText && IsEditorDirty;

    /// <summary>Queues the edited text as the new content of the file (it is written to the RPF by Save RPF).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyEdit))]
    private async Task ApplyEditAsync()
    {
        Rpf6Archive? archive = _archive;
        RpfNode? node = _viewerNode;
        TextDocument? document = _textDocument;
        if (archive == null || node == null || document == null)
        {
            return;
        }

        byte[] bytes = TextCodec.Encode(EditorText, document.Format, out bool encodingChanged);
        if (encodingChanged &&
            !await _dialogs.ConfirmAsync("Encoding will change",
                "The text has characters the file's original encoding (Latin-1) cannot store, so it will be saved as UTF-8. The game may not read UTF-8 in this file. Continue?",
                "Continue", "Cancel"))
        {
            return;
        }

        await RunAsync($"Applying changes to {node.Name}…", async ct =>
        {
            string editPath = NewEditPath();
            await File.WriteAllBytesAsync(editPath, bytes, ct);
            ApplyReplacementFile(archive, node, editPath);

            _viewerData = bytes;
            _textDocument = TextCodec.Decode(bytes);
            _originalText = _textDocument.Text;
            IsEditorDirty = false;
            _hexPage = 0;
            UpdateHex();
            ViewerInfo = $"{SizeFormatter.Format(bytes.Length)} · edited · {RpfHash.FormatHexHash(node.NameHash)}";

            ShowDirectory(_currentDirectory ?? archive.Root, SelectedNode?.Node, keepSearch: true);
            StatusMessage = $"{node.Name} updated in the archive. Press Save RPF to write it.";
        });
    }

    /// <summary>Closes the viewer (asks first if there is text or table entries you have not applied).</summary>
    [RelayCommand]
    private async Task CloseViewerAsync()
    {
        if (!IsViewerOpen)
        {
            return;
        }

        bool unapplied = (IsEditorDirty && CanEditText) || (HasTableEdits && TableEditable);
        if (unapplied &&
            !await _dialogs.ConfirmAsync("Discard your edits?", "You changed this file but did not apply the changes to the archive.", "Discard", "Keep editing"))
        {
            return;
        }

        IsViewerOpen = false;
        _loadingEditor = true;
        EditorText = string.Empty;
        _loadingEditor = false;
        IsEditorDirty = false;
        HexText = string.Empty;
        _viewerData = null;
        _viewerNode = null;
        _textDocument = null;
        ClearTable();
    }

    /// <summary>Android back button: closes the viewer first, then goes up a folder. Returns true if it handled the press.</summary>
    public bool HandleBackPressed()
    {
        if (IsViewerOpen)
        {
            _ = CloseViewerAsync();
            return true;
        }

        if (IsMultiSelect)
        {
            IsMultiSelect = false;   // leave multi-select mode first; the ticks are kept
            return true;
        }

        if (CanGoUp)
        {
            GoUp();
            return true;
        }

        return false;
    }
}
