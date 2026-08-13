using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Media;

public interface IBackupServiceFactory
{
    IBackupService Create(DeviceId device, IGalleryService gallery, ISettingsService settings);
}

public sealed class BackupServiceFactory(ILoggerFactory loggers) : IBackupServiceFactory
{
    public IBackupService Create(DeviceId device, IGalleryService gallery, ISettingsService settings)
        => new BackupService(device, gallery, settings, loggers.CreateLogger<BackupService>());
}

public sealed class BackupService(
    DeviceId device,
    IGalleryService gallery,
    ISettingsService settings,
    ILogger<BackupService> logger) : IBackupService
{
    /// <summary>Ceiling on one plan, so a first-ever backup of a full phone stays answerable.</summary>
    private const int MaxItems = 100_000;

    public async Task<BackupPlan> PlanAsync(
        DevicePath? scope,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var mark = since;

        if (mark is null)
        {
            var (at, _) = await GetLastBackupAsync(cancellationToken).ConfigureAwait(false);
            mark = at;
        }

        var candidates = scope is { } folder
            ? await gallery.GetAlbumContentsAsync(folder, cancellationToken).ConfigureAwait(false)
            : await gallery.GetTimelineAsync(null, 0, MaxItems, cancellationToken).ConfigureAwait(false);

        // Photos and videos only: audio and documents are not what "camera backup" means.
        var media = candidates
            .Where(item => item.Kind is MediaKind.Image or MediaKind.Video)
            .ToList();

        var newer = mark is { } threshold
            ? media.Where(item => CaptureTime(item) > threshold).ToList()
            : media;

        // Oldest first, so an interrupted backup leaves a contiguous run and the mark stays meaningful.
        var ordered = newer.OrderBy(CaptureTime).ToList();

        logger.LogInformation(
            "Backup plan: {Count} of {Total} items are newer than the last backup.",
            ordered.Count, media.Count);

        return new BackupPlan
        {
            DeviceId = device,
            Items = ordered,
            Since = mark,
            NewestItem = ordered.Count > 0 ? CaptureTime(ordered[^1]) : mark,
        };
    }

    public async Task RecordAsync(
        BackupPlan plan,
        string destinationFolder,
        CancellationToken cancellationToken)
    {
        if (plan.NewestItem is null)
        {
            return;
        }

        var profile = await settings.GetProfileAsync(device, cancellationToken).ConfigureAwait(false);

        // Never move the mark backwards: a scoped or date-limited backup must not make a later full backup
        // skip items it has not actually copied.
        var advanced = profile.LastBackupAt is { } existing && existing > plan.NewestItem
            ? existing
            : plan.NewestItem;

        await settings.SaveProfileAsync(
                profile with { LastBackupAt = advanced, LastBackupFolder = destinationFolder },
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation("Backup mark advanced.");
    }

    public async Task<(DateTimeOffset? At, string? Folder)> GetLastBackupAsync(
        CancellationToken cancellationToken)
    {
        var profile = await settings.GetProfileAsync(device, cancellationToken).ConfigureAwait(false);
        return (profile.LastBackupAt, profile.LastBackupFolder);
    }

    public string GetRelativeFolder(MediaItem item)
    {
        var date = CaptureTime(item).ToLocalTime();
        return Path.Combine(date.ToString("yyyy"), date.ToString("yyyy-MM"));
    }

    /// <summary>EXIF capture date where known, else the file's own time (spec §25).</summary>
    private static DateTimeOffset CaptureTime(MediaItem item) => item.DateTaken ?? item.Modified;
}

