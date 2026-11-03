using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MagicRDR.App.Models;
using MagicRDR.Core;

namespace MagicRDR.App.ViewModels;

/// <summary>
/// Sorting of the file list. <see cref="ShowDirectory"/> still builds the rows (with its default order); right after
/// <see cref="FileTreeNodes"/> is assigned, <see cref="OnFileTreeNodesChanged"/> puts the rows in the chosen order,
/// before the list is shown. The ".." row stays first, folders stay above files, and the rows themselves are only
/// reordered, so the multi-select ticks and the filter are not touched.
/// The same hook also asks for hashed names to be resolved automatically (see MainViewModel.Resolve.cs).
/// </summary>
public partial class MainViewModel
{
    private static readonly NodeSortMode[] SortModes =
    {
        NodeSortMode.NameAscending,
        NodeSortMode.NameDescending,
        NodeSortMode.SizeAscending,
        NodeSortMode.SizeDescending,
        NodeSortMode.TypeAscending,
        NodeSortMode.TypeDescending,
        NodeSortMode.OriginalOrder,
        NodeSortMode.OriginalOrderReversed,
    };

    /// <summary>The texts of the sort picker, in the same order as <see cref="SortModes"/>.</summary>
    public string[] SortChoices { get; } =
    {
        "Name: A → Z",
        "Name: Z → A",
        "Size: Smallest → Largest",
        "Size: Largest → Smallest",
        "Type: A → Z (extension)",
        "Type: Z → A (extension)",
        "Original order (archive)",
        "Original order: reversed",
    };

    [ObservableProperty]
    private int _selectedSortIndex;

    private NodeSortMode _sortMode = NodeSortMode.NameAscending;

    partial void OnSelectedSortIndexChanged(int value)
    {
        if (value < 0 || value >= SortModes.Length)
        {
            // The picker can report "nothing selected"; put the current choice back.
            SelectedSortIndex = Array.IndexOf(SortModes, _sortMode);
            return;
        }

        NodeSortMode mode = SortModes[value];
        if (mode == _sortMode)
        {
            return;
        }

        _sortMode = mode;
        if (_currentDirectory != null)
        {
            ShowDirectory(_currentDirectory, SelectedNode?.Node, keepSearch: true);
        }
    }

    /// <summary>Runs every time a new list of rows is assigned, before the list sees it.</summary>
    partial void OnFileTreeNodesChanged(ObservableCollection<RpfNodeItem> value)
    {
        ApplySort(value);
        ScheduleHashResolution();
    }

    private void ApplySort(ObservableCollection<RpfNodeItem> rows)
    {
        RpfNode? directory = _currentDirectory;
        if (directory == null || rows.Count < 2)
        {
            return;
        }

        // Position of every child in the archive's table of contents (used by "Original order").
        var order = new Dictionary<RpfNode, int>(directory.Children.Count);
        int position = 0;
        foreach (RpfNode child in directory.Children)
        {
            order[child] = position++;
        }

        RpfNodeItem? parentRow = rows.FirstOrDefault(r => r.IsParentLink);
        List<RpfNodeItem> entries = rows.Where(r => !r.IsParentLink).ToList();

        List<RpfNodeItem> sorted = NodeSorter.Sort(
            entries,
            item => NodeSorter.KeyOf(item.Node, order.TryGetValue(item.Node, out int index) ? index : int.MaxValue, _names),
            _sortMode);

        // The collection is not on screen yet, so rewriting it here costs nothing visible.
        rows.Clear();
        if (parentRow != null)
        {
            rows.Add(parentRow);
        }

        foreach (RpfNodeItem item in sorted)
        {
            rows.Add(item);
        }
    }
}
