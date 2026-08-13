using Handspan.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Handspan.App.Views;

public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();

        // Double-click opens a folder, matching Explorer and Finder (spec §7).
        AddHandler(DoubleTappedEvent, OnDoubleTapped, handledEventsToo: true);

        // Drag and drop from Explorer or Finder into the current folder (spec §31). Dragging device
        // files back out is the harder direction and uses IShellDragService (phase 6).
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ExplorerViewModel viewModel)
        {
            return;
        }

        // Only react to a double-click that landed on a row.
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is null)
        {
            return;
        }

        if (viewModel.SelectedEntry is { } entry)
        {
            viewModel.OpenCommand.Execute(entry);
        }
    }

    /// <summary>
    /// Mirrors the list's selection into the view model (spec §31).
    /// </summary>
    /// <remarks>
    /// The list owns selection; binding <c>SelectedItems</c> two-way is awkward to do reliably, so the
    /// selection is pushed across on change instead.
    /// </remarks>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ExplorerViewModel viewModel && sender is ListBox list)
        {
            viewModel.UpdateSelection(list.SelectedItems?.OfType<EntryRowViewModel>() ?? []);
        }
    }

    /// <summary>
    /// Picks local files to upload into the folder on screen (spec §9).
    /// </summary>
    /// <remarks>
    /// The picker lives here because Avalonia exposes it through the window rather than as an injectable
    /// service, and reaching the window is a view concern.
    /// </remarks>
    private async void OnUploadFilesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ExplorerViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose files to copy to the device",
            AllowMultiple = true,
        });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();

        if (paths.Count > 0)
        {
            await viewModel.UploadAsync(paths);
        }
    }

    /// <summary>Picks a local folder to upload, keeping its structure on the device.</summary>
    private async void OnUploadFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ExplorerViewModel viewModel
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to copy to the device",
            AllowMultiple = true,
        });

        var paths = folders
            .Select(folder => folder.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();

        if (paths.Count > 0)
        {
            await viewModel.UploadAsync(paths);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var acceptable = DataContext is ExplorerViewModel { HasSession: true }
                         && e.DataTransfer.Contains(DataFormat.File);

        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not ExplorerViewModel viewModel
            || !e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        var paths = e.DataTransfer.TryGetValues(DataFormat.File)?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .ToList();

        if (paths is { Count: > 0 })
        {
            // Fire and forget: the upload is queued and tracked on the Transfers page.
            _ = viewModel.UploadAsync(paths);
        }
    }
}
