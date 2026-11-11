using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows.Models;

namespace ShippingPrinter.Windows.Services;

/// <summary>Session-only history. Snapshots retain bitmap references, never source files.</summary>
public sealed class WorkspaceHistory
{
    private sealed record Entry(string Name, Snapshot Before, Snapshot After);
    public sealed record LabelState(LabelItem Item, BitmapSource Image);
    public sealed record Snapshot(LabelState[] Labels, LabelItem[] Selection)
    {
        public void Restore(ObservableCollection<LabelItem> items)
        {
            items.Clear();
            foreach (var state in Labels)
            {
                state.Item.Image = state.Image;
                items.Add(state.Item);
            }
        }
    }

    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    public string? UndoName => _undo.LastOrDefault()?.Name;
    public string? RedoName => _redo.LastOrDefault()?.Name;

    public static Snapshot Capture(IEnumerable<LabelItem> items, IEnumerable<LabelItem> selection) =>
        new(items.Select(item => new LabelState(item, item.Image)).ToArray(), selection.ToArray());

    public void Record(string name, Snapshot before, Snapshot after)
    {
        // Selection changes and resetting an untouched label are not edits.
        if (before.Labels.SequenceEqual(after.Labels)) return;
        _redo.Clear();
        _undo.Add(new(name, before, after));
        // Count both snapshots and originals conservatively, even when they share
        // images. Cropping must not hide the cost of a retained full-size original.
        // Large workspaces retain fewer steps; the latest action stays undoable.
        long Pixels(Entry entry) => entry.Before.Labels.Concat(entry.After.Labels)
            .Sum(state => (long)state.Image.PixelWidth * state.Image.PixelHeight +
                (long)state.Item.Original.PixelWidth * state.Item.Original.PixelHeight);
        var pixels = _undo.Sum(Pixels);
        while (_undo.Count > 1 && (_undo.Count > 20 || pixels > 128_000_000))
        {
            pixels -= Pixels(_undo[0]);
            _undo.RemoveAt(0);
        }
    }

    public Snapshot? Undo()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        return entry.Before;
    }

    public Snapshot? Redo()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        return entry.After;
    }
}
