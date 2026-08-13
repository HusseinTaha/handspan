using Handspan.Core.Exceptions;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Microsoft.Extensions.Logging;

namespace Handspan.Media;

/// <summary>Creates a gallery service per device session.</summary>
public interface IGalleryServiceFactory
{
    IGalleryService Create(DeviceId device, IDeviceFileSystem fileSystem);
}

public sealed class GalleryServiceFactory(
    IMediaIndexStore index,
    ILoggerFactory loggers) : IGalleryServiceFactory
{
    public IGalleryService Create(DeviceId device, IDeviceFileSystem fileSystem)
        => new GalleryService(device, fileSystem, index, loggers.CreateLogger<GalleryService>());
}

/// <summary>
/// The gallery: a media index plus virtual albums (spec §18–§26, §60).
/// </summary>
/// <remarks>
/// Reads come from the index so the gallery opens instantly; <see cref="RefreshAsync"/> rescans the device
/// and updates it. Scan roots are configurable and are never assumed to be the only places media lives —
/// OEMs and messaging apps scatter it widely (spec §19, §26).
/// </remarks>
public sealed class GalleryService(
    DeviceId device,
    IDeviceFileSystem fileSystem,
    IMediaIndexStore index,
    ILogger<GalleryService> logger) : IGalleryService
{
    /// <summary>Directories never worth scanning for media: huge, permission-fraught, uninteresting.</summary>
    private static readonly string[] SkippedFolders = ["Android", ".thumbnails", "cache", ".trashed"];

    public DeviceId DeviceId => device;

    public IReadOnlyList<DevicePath> Sources { get; set; } = KnownPaths.DefaultGallerySources;

    public Task<IReadOnlyList<MediaItem>> GetTimelineAsync(
        MediaKind? filter,
        int skip,
        int take,
        CancellationToken cancellationToken)
        => index.QueryAsync(device, filter, skip, take, cancellationToken);

    /// <summary>
    /// Virtual albums, derived from the directories that actually contain media (spec §26).
    /// </summary>
    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken cancellationToken)
    {
        var folders = await index.QueryFoldersAsync(device, cancellationToken).ConfigureAwait(false);

        // Names must be disambiguated against each other, not chosen in isolation. A real phone has
        // /sdcard/Pictures/Telegram, /sdcard/Movies/Telegram and more besides, plus Screenshots in two
        // places — naming each by its own folder alone produces a list of identical entries.
        var names = DisambiguateNames(folders.Select(folder => folder.Folder).ToList());

        var albums = new List<Album>(folders.Count);

        foreach (var (folder, count, bytes, newest) in folders)
        {
            var contents = await index.QueryFolderAsync(device, folder, cancellationToken)
                .ConfigureAwait(false);

            albums.Add(new Album
            {
                DeviceId = device,
                Path = folder,
                Name = names[folder],
                ItemCount = count,
                TotalBytes = bytes,
                NewestItem = newest,
                Cover = contents.FirstOrDefault(item => item.Kind == MediaKind.Image)
                        ?? contents.FirstOrDefault(),
            });
        }

        return albums;
    }

    /// <summary>
    /// Gives every album a name that is unique within the list, adding ancestry only where needed.
    /// </summary>
    /// <remarks>
    /// Qualifying every album with its parent would be noise — "DCIM · Camera" says nothing "Camera" does
    /// not. So the short name is used wherever it is unambiguous, and only collisions grow a prefix, one
    /// ancestor at a time until they separate.
    /// </remarks>
    private static Dictionary<DevicePath, string> DisambiguateNames(IReadOnlyList<DevicePath> folders)
    {
        var names = folders.ToDictionary(folder => folder, DescribeAlbum);

        for (var depth = 1; depth <= 3; depth++)
        {
            var collisions = names
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(pair => pair.Key))
                .ToList();

            if (collisions.Count == 0)
            {
                break;
            }

            foreach (var folder in collisions)
            {
                names[folder] = QualifyWithAncestors(folder, depth);
            }
        }

        return names;
    }

    /// <summary>Builds "Parent · Name", walking up as far as <paramref name="depth"/> ancestors.</summary>
    private static string QualifyWithAncestors(DevicePath folder, int depth)
    {
        var segments = new List<string> { DescribeAlbum(folder) };
        var current = folder.Parent;

        for (var i = 0; i < depth && !current.IsRoot; i++)
        {
            // "Internal Storage · Telegram" is less useful than plain "Telegram"; stop at the storage root.
            if (current == KnownPaths.InternalStorage)
            {
                break;
            }

            segments.Insert(0, current.Name);
            current = current.Parent;
        }

        return string.Join(" · ", segments);
    }

    public Task<IReadOnlyList<MediaItem>> GetAlbumContentsAsync(
        DevicePath albumPath,
        CancellationToken cancellationToken)
        => index.QueryFolderAsync(device, albumPath, cancellationToken);

    /// <summary>Rescans the configured sources and replaces the index.</summary>
    public async Task RefreshAsync(IProgress<int>? scannedCount, CancellationToken cancellationToken)
    {
        var items = new List<MediaItem>();

        foreach (var source in Sources)
        {
            await ScanAsync(source, items, scannedCount, cancellationToken).ConfigureAwait(false);
        }

        await index.ReplaceAsync(device, items, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Gallery scan complete: {Count} media items indexed.", items.Count);
    }

    private async Task ScanAsync(
        DevicePath root,
        List<MediaItem> items,
        IProgress<int>? scannedCount,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<DevicePath>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            IReadOnlyList<DeviceEntry> entries;
            try
            {
                entries = await fileSystem.ListAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // A missing or protected source is normal: not every device has every folder.
                continue;
            }

            // A .nomedia file marks a directory as excluded from media scanning by convention.
            if (entries.Any(entry => entry.Name == ".nomedia"))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (!SkippedFolders.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                        && !entry.Name.StartsWith('.'))
                    {
                        queue.Enqueue(entry.Path);
                    }

                    continue;
                }

                var kind = MediaTypes.FromPath(entry.Path);
                if (kind is MediaKind.None or MediaKind.Document)
                {
                    continue;
                }

                items.Add(new MediaItem
                {
                    DeviceId = device,
                    Path = entry.Path,
                    Kind = kind,
                    Size = entry.Size,
                    Modified = entry.Modified,
                });

                if (items.Count % 100 == 0)
                {
                    scannedCount?.Report(items.Count);
                }
            }
        }

        scannedCount?.Report(items.Count);
    }

    /// <summary>
    /// Names an album from its path, recognising the conventional folders without assuming them.
    /// </summary>
    private static string DescribeAlbum(DevicePath folder)
    {
        var name = folder.Name;

        // A bare "Media" or "Sent" tells the user nothing; the parent gives it meaning, which is how
        // WhatsApp and Telegram folders read sensibly.
        if (name is "Media" or "Sent" or "Images" or "Video" or "Videos" && !folder.Parent.IsRoot)
        {
            return $"{folder.Parent.Name} · {name}";
        }

        return name;
    }
}
