using Handspan.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Handspan.App.Views;

public partial class GalleryView : UserControl
{
    public GalleryView() => InitializeComponent();

    /// <summary>
    /// Loads a tile's thumbnail when the tile is actually realized.
    /// </summary>
    /// <remarks>
    /// This is the viewport-driven loading the gallery needs (spec §22). Requesting thumbnails for the whole
    /// collection instead would queue tens of thousands of device reads for photos nobody is looking at — and
    /// requesting them when the <em>view</em> loads does nothing at all, because the timeline is filled
    /// asynchronously after that point. Hanging it off the container's Loaded event means a tile fetches
    /// exactly when it first appears, and never before.
    /// </remarks>
    private void OnTileLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            _ = item.LoadThumbnailAsync();
        }
    }

    /// <summary>
    /// Routes a tap to the view model with its keyboard modifiers, for select-or-open (spec §31).
    /// </summary>
    /// <remarks>
    /// Handled in code-behind rather than by a command bound through the template. The tiles sit inside two
    /// nested item repeaters, so <c>$parent[ItemsControl]</c> resolves to the inner one, whose data context is
    /// a date group rather than the gallery — the cast fails silently and the button does nothing. Resolving
    /// the view's own data context here cannot fail that way, and it is also the only place the modifier keys
    /// are available.
    /// </remarks>
    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not GalleryViewModel gallery
            || sender is not Control { DataContext: GalleryItemViewModel item })
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        gallery.HandleTap(item, control, shift);
        e.Handled = true;
    }

    /// <summary>Toggles selection from the tile's checkbox, without opening the item.</summary>
    private void OnSelectTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is GalleryViewModel gallery
            && sender is Control { DataContext: GalleryItemViewModel item })
        {
            // Always a pure toggle: the checkbox exists precisely so selecting needs no modifier keys.
            gallery.HandleTap(item, control: true, shift: false);
            e.Handled = true;
        }
    }
}
