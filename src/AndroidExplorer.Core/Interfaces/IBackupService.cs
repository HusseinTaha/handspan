using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Interfaces;

/// <summary>
/// What an incremental backup would copy.
/// </summary>
public sealed record BackupPlan
{
    public required DeviceId DeviceId { get; init; }

    public required IReadOnlyList<MediaItem> Items { get; init; }

    /// <summary>The high-water mark this plan was built against; null means nothing has been backed up.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Capture time of the newest item, which becomes the new mark once the copy succeeds.</summary>
    public DateTimeOffset? NewestItem { get; init; }

    public long TotalBytes => Items.Sum(item => item.Size);

    public int PhotoCount => Items.Count(item => item.Kind == MediaKind.Image);

    public int VideoCount => Items.Count(item => item.Kind == MediaKind.Video);

    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// Copies everything new since the last backup (spec §92, "Phone to PC backup").
/// </summary>
/// <remarks>
/// <para>
/// This is the reason most people plug a phone into a computer, and doing it by hand does not scale — the test
/// device holds 8,099 media items. The index already knows what exists and when it was taken, so the diff
/// costs nothing.
/// </para>
/// <para>
/// "New" is decided by a stored high-water mark, not by comparing against the destination folder. Comparing
/// would re-copy everything the user has since moved, renamed or deliberately deleted on their PC, which for
/// a photo library is precisely the wrong behaviour. The trade-off is the opposite failure: a photo that
/// arrives on the phone with an older capture date than the mark is not picked up, which is why a plan can
/// also be built from an explicit date.
/// </para>
/// </remarks>
public interface IBackupService
{
    /// <summary>Works out what would be copied, without copying anything.</summary>
    Task<BackupPlan> PlanAsync(DevicePath? scope, DateTimeOffset? since, CancellationToken cancellationToken);

    /// <summary>Records a completed backup so the next one starts where this finished.</summary>
    Task RecordAsync(BackupPlan plan, string destinationFolder, CancellationToken cancellationToken);

    /// <summary>The mark left by the previous backup, if any.</summary>
    Task<(DateTimeOffset? At, string? Folder)> GetLastBackupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Local folder for an item, grouped by capture date.
    /// </summary>
    /// <remarks>
    /// A flat dump of 8,000 photos into one directory is unusable, and every photo tool groups by date, so
    /// <c>2026/2026-08</c> is what people already expect to find.
    /// </remarks>
    string GetRelativeFolder(MediaItem item);
}
