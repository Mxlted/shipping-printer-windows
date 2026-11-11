using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShippingPrinter.Windows.Models;
using ShippingPrinter.Windows.Services;

namespace ShippingPrinter.Windows;

public partial class MainWindow
{
    private readonly WorkspaceHistory _history = new();
    private bool _workspaceUpdating;
    private bool HasSingleSelection => ItemsList.SelectedItems.Count == 1;
    private LabelItem[] SelectedLabels => _items.Where(item => ItemsList.SelectedItems.Contains(item)).ToArray();
    private WorkspaceHistory.Snapshot CaptureWorkspace() => WorkspaceHistory.Capture(_items, SelectedLabels);

    private void SelectLabels(IEnumerable<LabelItem> labels)
    {
        ItemsList.UnselectAll();
        foreach (var item in labels) ItemsList.SelectedItems.Add(item);
        if (Selected is not null) ItemsList.ScrollIntoView(Selected);
    }

    private void RestoreWorkspace(WorkspaceHistory.Snapshot snapshot)
    {
        _workspaceUpdating = true;
        try
        {
            snapshot.Restore(_items);
            SelectLabels(snapshot.Selection);
        }
        finally { _workspaceUpdating = false; }
        UpdatePreview();
        FocusLabels();
    }

    private void FocusLabels()
    {
        ItemsList.Focus();
        if (HasSingleSelection && ItemsList.ItemContainerGenerator.ContainerFromItem(Selected) is ListBoxItem row)
            row.Focus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var name = _history.UndoName;
        if (_history.Undo() is not { } snapshot) return;
        RestoreWorkspace(snapshot);
        Status($"Undone: {name}.");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var name = _history.RedoName;
        if (_history.Redo() is not { } snapshot) return;
        RestoreWorkspace(snapshot);
        Status($"Redone: {name}.");
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ItemsList.SelectAll();
        ItemsList.Focus();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !HasSingleSelection || Selected is null) return;
        try
        {
            Clipboard.SetImage(ImageService.RenderLabel(Selected.Image, ReadSettings()));
            Status("Label copied.");
        }
        catch (Exception ex) { Error(ex); }
    }

    private static bool CanPasteLabels()
    {
        try { return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage(); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
                await ImportPathsAsync(Clipboard.GetFileDropList().Cast<string>());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                AddGenerated(image, "Clipboard image", null);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void SavePng_Click(object sender, RoutedEventArgs e) => Export(false);
    private void SaveThermal_Click(object sender, RoutedEventArgs e) => Export(true);

    private void Items_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(ItemsList, source) is not ListBoxItem row) return;
        if (!row.IsSelected) ItemsList.SelectedItem = row.DataContext;
        // The list's default right-click handling collapses an extended selection.
        // Focus its surface, leaving the selected rows intact for the menu action.
        ItemsList.Focus();
        e.Handled = true;
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e) => PreviewSurface.Focus();

    private void WorkspaceMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        var surface = (FrameworkElement)sender;
        var menu = surface.ContextMenu;
        menu.Items.Clear();
        var onLabel = surface == PreviewSurface || e.CursorLeft < 0 ||
            (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ItemsList, source) is ListBoxItem);
        var canEdit = !_busy && Selected is not null;
        var single = canEdit && HasSingleSelection;
        if (onLabel && Selected is not null)
        {
            AddMenuItem(menu, "Print label…", "Ctrl+P", Print_Click, single && _rendered is not null);
            AddMenuItem(menu, "Save as PNG…", "Ctrl+S", SavePng_Click, single && _rendered is not null);
            AddMenuItem(menu, "Copy image", "Ctrl+C", Copy_Click, single && _rendered is not null);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "Crop…", "Ctrl+Shift+X", Crop_Click, single);
            AddMenuItem(menu, "Rotate left", "Ctrl+[", RotateLeft_Click, canEdit);
            AddMenuItem(menu, "Rotate right", "Ctrl+]", RotateRight_Click, canEdit);
            AddMenuItem(menu, "Reset edits", "", Reset_Click, canEdit && SelectedLabels.Any(item => !ReferenceEquals(item.Image, item.Original)));
            AddMenuItem(menu, "Read barcode", "", ReadBarcode_Click, single);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, ItemsList.SelectedItems.Count > 1 ? $"Remove {ItemsList.SelectedItems.Count} labels" : "Remove label", "Del", Remove_Click, canEdit);
        }
        else
        {
            AddMenuItem(menu, "Import files…", "Ctrl+O", Import_Click, !_busy);
            AddMenuItem(menu, "Paste", "Ctrl+V", Paste_Click, !_busy && CanPasteLabels());
            AddMenuItem(menu, "Create barcode…", "", Barcode_Click, !_busy);
        }
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Undo", "Ctrl+Z", Undo_Click, !_busy && _history.UndoName is not null);
        AddMenuItem(menu, "Redo", "Ctrl+Y", Redo_Click, !_busy && _history.RedoName is not null);
        AddMenuItem(menu, "Select all labels", "Ctrl+A", SelectAll_Click, !_busy && _items.Count > 0);
    }

    private static void AddMenuItem(ContextMenu menu, string title, string shortcut, RoutedEventHandler action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, InputGestureText = shortcut, IsEnabled = enabled };
        item.Click += action;
        menu.Items.Add(item);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.O) { Import_Click(sender, e); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && e.Key == Key.P) { Print_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Escape && modifiers == ModifierKeys.None && _busy)
        { _operation?.Cancel(); e.Handled = true; return; }

        // Leave native text editing, slider keys, and open menus to their controls.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or Slider or MenuItem ||
            Keyboard.FocusedElement is DependencyObject focused && FindTextInput(focused)) return;
        if (_busy) return;
        if (modifiers == ModifierKeys.None && e.Key == Key.Delete) Remove_Click(sender, e);
        else if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.A: SelectAll_Click(sender, e); break;
                case Key.C: Copy_Click(sender, e); break;
                case Key.V: Paste_Click(sender, e); break;
                case Key.S: Export(false); break;
                case Key.Z: Undo_Click(sender, e); break;
                case Key.Y: Redo_Click(sender, e); break;
                case Key.OemOpenBrackets: RotateLeft_Click(sender, e); break;
                case Key.OemCloseBrackets: RotateRight_Click(sender, e); break;
                default: return;
            }
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z) Redo_Click(sender, e);
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.X) Crop_Click(sender, e);
        else return;
        e.Handled = true;
    }

    private static bool FindTextInput(DependencyObject element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase or PasswordBox or ComboBox or Slider) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
}
