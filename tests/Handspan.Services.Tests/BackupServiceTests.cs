using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Services.Tests;

/// <summary>
/// Incremental backup (spec §92).
/// </summary>
/// <remarks>
/// The rules that decide what counts as "new" are the whole feature, and getting them wrong is expensive in
/// both directions: too eager re-copies gigabytes the user already has, too lazy silently skips photos and the
/// user only finds out when they need one.
/// </remarks>
public sealed class BackupServiceTests
{
    private static readonly DeviceId Device = new("backupDevice");

    private static DateTimeOffset At(int day) =>
        new(2026, 8, day, 12, 0, 0, TimeSpan.Zero);

    private static MediaItem Item(string name, DateTimeOffset taken, MediaKind kind = MediaKind.Image,
        long size = 2_000_000) => new()
    {
        DeviceId = Device,
        Path = KnownPaths.Camera.Combine(name),
        Kind = kind,
        Size = size,
        Modified = taken,
        DateTaken = taken,
    };

    private static (BackupService Backup, StubSettings Settings) Create(params MediaItem[] items)
    {
        var settings = new StubSettings();
        var backup = new BackupService(
            Device, new StubGallery(items), settings, NullLogger<BackupService>.Instance);

        return (backup, settings);
    }

    [Fact]
    public async Task A_first_backup_offers_everything()
    {
        var (backup, _) = Create(
            Item("a.jpg", At(1)), Item("b.jpg", At(2)), Item("c.mp4", At(3), MediaKind.Video));

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);

        Assert.Equal(3, plan.Items.Count);
        Assert.Null(plan.Since);
        Assert.Equal(2, plan.PhotoCount);
        Assert.Equal(1, plan.VideoCount);
        Assert.Equal(6_000_000, plan.TotalBytes);
    }

    [Fact]
    public async Task Only_items_newer_than_the_mark_are_offered()
    {
        var (backup, settings) = Create(
            Item("old.jpg", At(1)), Item("edge.jpg", At(5)), Item("new.jpg", At(9)));

        // A previous backup reached the 5th.
        settings.Profile = settings.Profile with { LastBackupAt = At(5) };

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);

        // Strictly newer: the item exactly at the mark was already copied.
        var only = Assert.Single(plan.Items);
        Assert.Equal("new.jpg", only.Name);
        Assert.Equal(At(5), plan.Since);
    }

    [Fact]
    public async Task Nothing_new_gives_an_empty_plan()
    {
        var (backup, settings) = Create(Item("a.jpg", At(1)), Item("b.jpg", At(2)));
        settings.Profile = settings.Profile with { LastBackupAt = At(30) };

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.TotalBytes);
    }

    [Fact]
    public async Task An_explicit_date_overrides_the_stored_mark()
    {
        var (backup, settings) = Create(
            Item("a.jpg", At(1)), Item("b.jpg", At(5)), Item("c.jpg", At(9)));

        settings.Profile = settings.Profile with { LastBackupAt = At(8) };

        // Asking from the beginning is how "copy everything again" onto a fresh disk works.
        var plan = await backup.PlanAsync(null, DateTimeOffset.MinValue, CancellationToken.None);

        Assert.Equal(3, plan.Items.Count);
    }

    [Fact]
    public async Task Audio_and_documents_are_not_part_of_a_camera_backup()
    {
        var (backup, _) = Create(
            Item("photo.jpg", At(1)),
            Item("clip.mp4", At(2), MediaKind.Video),
            Item("song.mp3", At(3), MediaKind.Audio),
            Item("notes.pdf", At(4), MediaKind.Document));

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item => Assert.True(item.Kind is MediaKind.Image or MediaKind.Video));
    }

    [Fact]
    public async Task Items_are_ordered_oldest_first()
    {
        var (backup, _) = Create(Item("c.jpg", At(9)), Item("a.jpg", At(1)), Item("b.jpg", At(5)));

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);

        // Oldest first means an interrupted backup leaves a contiguous run, so the mark stays meaningful.
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], plan.Items.Select(item => item.Name));
        Assert.Equal(At(9), plan.NewestItem);
    }

    [Fact]
    public async Task Recording_advances_the_mark_to_the_newest_item()
    {
        var (backup, settings) = Create(Item("a.jpg", At(1)), Item("b.jpg", At(7)));

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);
        await backup.RecordAsync(plan, @"D:\Backup", CancellationToken.None);

        Assert.Equal(At(7), settings.Profile.LastBackupAt);
        Assert.Equal(@"D:\Backup", settings.Profile.LastBackupFolder);

        // And the next run then finds nothing.
        var second = await backup.PlanAsync(null, null, CancellationToken.None);
        Assert.True(second.IsEmpty);
    }

    [Fact]
    public async Task The_mark_never_moves_backwards()
    {
        var (backup, settings) = Create(Item("old.jpg", At(2)));
        settings.Profile = settings.Profile with { LastBackupAt = At(20) };

        // A backup limited to older items must not rewind the mark, or a later full run would skip
        // everything between — items it never actually copied.
        var plan = await backup.PlanAsync(null, DateTimeOffset.MinValue, CancellationToken.None);
        await backup.RecordAsync(plan, @"D:\Backup", CancellationToken.None);

        Assert.Equal(At(20), settings.Profile.LastBackupAt);
    }

    [Fact]
    public async Task An_empty_plan_leaves_the_mark_alone()
    {
        var (backup, settings) = Create();
        settings.Profile = settings.Profile with { LastBackupAt = At(10) };

        var plan = await backup.PlanAsync(null, null, CancellationToken.None);
        await backup.RecordAsync(plan, @"D:\Backup", CancellationToken.None);

        Assert.Equal(At(10), settings.Profile.LastBackupAt);
    }

    [Theory]
    [InlineData(2026, 8, 13, @"2026\2026-08")]
    [InlineData(2024, 1, 1, @"2024\2024-01")]
    [InlineData(2025, 12, 31, @"2025\2025-12")]
    public void Items_are_grouped_into_year_and_month_folders(int year, int month, int day, string expected)
    {
        var (backup, _) = Create();

        var item = Item("x.jpg", new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

        // A flat dump of thousands of photos is unusable, and dated folders are what people expect.
        Assert.Equal(expected.Replace('\\', Path.DirectorySeparatorChar),
            backup.GetRelativeFolder(item));
    }

    [Fact]
    public void Capture_date_is_preferred_over_file_time_for_grouping()
    {
        var (backup, _) = Create();

        // A photo copied between phones keeps its capture date but gains a new file time; it belongs in the
        // month it was taken (spec §25).
        var item = Item("x.jpg", At(13)) with
        {
            DateTaken = new DateTimeOffset(2019, 3, 4, 10, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal(
            Path.Combine("2019", "2019-03"),
            backup.GetRelativeFolder(item));
    }

    [Fact]
    public async Task The_last_backup_is_reported_for_the_ui()
    {
        var (backup, settings) = Create();
        settings.Profile = settings.Profile with
        {
            LastBackupAt = At(11),
            LastBackupFolder = @"E:\Photos",
        };

        var (at, folder) = await backup.GetLastBackupAsync(CancellationToken.None);

        Assert.Equal(At(11), at);
        Assert.Equal(@"E:\Photos", folder);
    }

    // ---------------- stubs ----------------

    private sealed class StubGallery(IReadOnlyList<MediaItem> items) : IGalleryService
    {
        public DeviceId DeviceId => Device;

        public IReadOnlyList<DevicePath> Sources { get; set; } = [];

        public Task<IReadOnlyList<MediaItem>> GetTimelineAsync(
            MediaKind? filter, int skip, int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MediaItem>>(items
                .Where(item => filter is null || item.Kind == filter)
                .Skip(skip)
                .Take(take)
                .ToList());

        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Album>>([]);

        public Task<IReadOnlyList<MediaItem>> GetAlbumContentsAsync(
            DevicePath albumPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MediaItem>>(items
                .Where(item => item.Path.Parent == albumPath)
                .ToList());

        public Task RefreshAsync(IProgress<int>? scannedCount, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubSettings : ISettingsService
    {
        public DeviceProfile Profile { get; set; } = new() { DeviceId = Device };

        public AppSettings Current { get; } = new();

        public event EventHandler<AppSettings>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken)
        {
            _ = Changed;
            return Task.CompletedTask;
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<DeviceProfile> GetProfileAsync(DeviceId deviceId, CancellationToken cancellationToken)
            => Task.FromResult(Profile);

        public Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken)
        {
            Profile = profile;
            return Task.CompletedTask;
        }
    }
}
