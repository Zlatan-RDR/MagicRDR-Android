using CommunityToolkit.Mvvm.ComponentModel;

namespace MagicRDR.App.Models;

/// <summary>One row of the string-table editor. Only the text can change; index and key identify the entry.</summary>
public sealed partial class TableRowItem : ObservableObject
{
    private const int PreviewLength = 140;

    public TableRowItem(int index, string keyText, string value, string original)
    {
        Index = index;
        KeyText = keyText;
        Original = original ?? string.Empty;
        _value = value ?? string.Empty;
    }

    public int Index { get; }

    /// <summary>The key as <c>0x1A2B3C4D</c> for hash tables, empty for language tables.</summary>
    public string KeyText { get; }

    /// <summary>The text as last applied to the archive (or as first loaded).</summary>
    public string Original { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private string _value;

    public string Header => KeyText.Length == 0 ? $"#{Index}" : $"#{Index} · {KeyText}";

    public string Preview
    {
        get
        {
            string flat = Value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ⏎ ", StringComparison.Ordinal);
            return flat.Length <= PreviewLength ? flat : flat.Substring(0, PreviewLength) + "…";
        }
    }

    public bool IsEdited => !string.Equals(Value, Original, StringComparison.Ordinal);

}
