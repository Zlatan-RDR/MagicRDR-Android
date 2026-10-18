using CommunityToolkit.Mvvm.ComponentModel;
using MagicRDR.Core;

namespace MagicRDR.App.Models;

/// <summary>
/// One row in the file list: a thin, read-only view of an <see cref="RpfNode"/> (or the ".." row that goes up a level).
/// The list is rebuilt whenever the directory or the archive changes. Only the multi-select state can change on a live row.
/// </summary>
public sealed partial class RpfNodeItem : ObservableObject
{
    public RpfNodeItem(RpfNode node, bool isParentLink = false)
    {
        Node = node;
        IsParentLink = isParentLink;
    }

    /// <summary>The node itself, or — for the ".." row — the parent directory it leads to.</summary>
    public RpfNode Node { get; }

    public bool IsParentLink { get; }

    public bool IsDirectory => Node.IsDirectory;

    public bool IsFile => !IsParentLink && !Node.IsDirectory;

    /// <summary>Show the "Open" button (folders and the ".." row).</summary>
    public bool CanOpen => Node.IsDirectory;

    /// <summary>Show the "View" button (files): opens the viewer / editor.</summary>
    public bool CanView => IsFile;

    public bool IsModified => IsFile && Node.IsModified;

    /// <summary>Ticked in multi-select mode.</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>The tick box is only shown on files, and only while multi-select mode is on.</summary>
    [ObservableProperty]
    private bool _showCheckBox;

    public string Name => IsParentLink ? ".." : Node.Name;

    public string Glyph => IsParentLink ? "↰" : Node.IsDirectory ? "📁" : Node.IsResource ? "📦" : "📄";

    public string Detail
    {
        get
        {
            if (IsParentLink)
            {
                return $"Back to {Node.Name}";
            }

            if (Node.IsDirectory)
            {
                int count = Node.Children.Count;
                return count == 1 ? "1 item" : $"{count:N0} items";
            }

            string kind = Node.IsResource ? "resource" : Node.IsCompressed ? "compressed" : "stored";
            return $"{SizeFormatter.Format(Node.UncompressedSize)} · {kind}{(Node.IsModified ? " · edited" : "")}";
        }
    }
}

public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB" };

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.#} {Units[unit]}";
    }
}
