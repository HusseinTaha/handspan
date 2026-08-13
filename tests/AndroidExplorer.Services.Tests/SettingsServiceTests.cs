using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Core.Platform;
using AndroidExplorer.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace AndroidExplorer.Services.Tests;

/// <summary>
/// Settings persistence and per-device profiles (spec §50, §65, §67).
/// </summary>
public sealed class SettingsServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ae-settings-{Guid.NewGuid():N}");

    private static readonly DeviceId DeviceA = new("profileA");
    private static readonly DeviceId DeviceB = new("profileB");

    private AndroidExplorerDatabase _database = null!;
    private IDeviceProfileStore _profiles = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _database = new AndroidExplorerDatabase(
            Path.Combine(_root, "test.db"), NullLogger<AndroidExplorerDatabase>.Instance);
        _profiles = new SqliteDeviceProfileStore(
            _database, NullLogger<SqliteDeviceProfileStore>.Instance);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private SettingsService CreateService()
        => new(new StubShell(_root), _profiles, NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task Settings_survive_a_restart()
    {
        var first = CreateService();
        await first.SaveAsync(first.Current with
        {
            MaxConcurrentSmallTransfers = 7,
            ShowHiddenFiles = true,
            Verification = VerificationMode.Sha256,
            AdbExecutablePath = @"C:\tools\adb.exe",
        }, CancellationToken.None);

        // A second instance reads the same file, as a relaunch would.
        var second = CreateService();
        await second.LoadAsync(CancellationToken.None);

        Assert.Equal(7, second.Current.MaxConcurrentSmallTransfers);
        Assert.True(second.Current.ShowHiddenFiles);
        Assert.Equal(VerificationMode.Sha256, second.Current.Verification);
        Assert.Equal(@"C:\tools\adb.exe", second.Current.AdbExecutablePath);
    }

    [Fact]
    public async Task Saving_raises_a_change_notification()
    {
        var service = CreateService();
        AppSettings? observed = null;
        service.Changed += (_, updated) => observed = updated;

        await service.SaveAsync(service.Current with { RetryCount = 5 }, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(5, observed!.RetryCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(999, 16)]
    public async Task Nonsense_concurrency_is_clamped(int written, int expected)
    {
        // The settings file is user-visible and will be hand-edited; zero concurrent transfers would stall
        // the queue forever.
        var service = CreateService();
        await service.SaveAsync(service.Current with { MaxConcurrentSmallTransfers = written },
            CancellationToken.None);

        Assert.Equal(expected, service.Current.MaxConcurrentSmallTransfers);
    }

    [Fact]
    public async Task Other_nonsense_values_are_clamped_too()
    {
        var service = CreateService();

        await service.SaveAsync(service.Current with
        {
            ThumbnailMaxEdgePixels = 0,
            RetryCount = -1,
            ConnectionTimeoutSeconds = 0,
            ThumbnailCacheCapBytes = 1,
        }, CancellationToken.None);

        Assert.InRange(service.Current.ThumbnailMaxEdgePixels, 64, 1024);
        Assert.Equal(0, service.Current.RetryCount);
        Assert.InRange(service.Current.ConnectionTimeoutSeconds, 3, 120);
        Assert.True(service.Current.ThumbnailCacheCapBytes >= 64L * 1024 * 1024);
    }

    [Fact]
    public async Task A_corrupt_settings_file_falls_back_to_defaults()
    {
        await System.IO.File.WriteAllTextAsync(
            Path.Combine(_root, "settings.json"), "{ this is not json");

        var service = CreateService();

        // Must not throw: a bad file cannot be allowed to stop the app from starting.
        await service.LoadAsync(CancellationToken.None);

        Assert.Equal(4, service.Current.MaxConcurrentSmallTransfers);
    }

    [Fact]
    public async Task Favorites_are_stored_per_device()
    {
        var service = CreateService();

        await _profiles.AddFavoriteAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);
        await _profiles.AddFavoriteAsync(DeviceA, KnownPaths.Download, CancellationToken.None);
        await _profiles.AddFavoriteAsync(DeviceB, KnownPaths.Documents, CancellationToken.None);

        var profileA = await service.GetProfileAsync(DeviceA, CancellationToken.None);
        var profileB = await service.GetProfileAsync(DeviceB, CancellationToken.None);

        // Spec §39/§67: pinned folders belong to one phone, not to all of them.
        Assert.Equal(2, profileA.Favorites.Count);
        Assert.Contains(KnownPaths.Camera, profileA.Favorites);
        Assert.DoesNotContain(KnownPaths.Documents, profileA.Favorites);

        Assert.Equal(KnownPaths.Documents, Assert.Single(profileB.Favorites));
    }

    [Fact]
    public async Task Adding_the_same_favorite_twice_is_harmless()
    {
        await _profiles.AddFavoriteAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);
        await _profiles.AddFavoriteAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        Assert.Single(await _profiles.GetFavoritesAsync(DeviceA, CancellationToken.None));
    }

    [Fact]
    public async Task Favorites_can_be_removed()
    {
        await _profiles.AddFavoriteAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);
        await _profiles.RemoveFavoriteAsync(DeviceA, KnownPaths.Camera, CancellationToken.None);

        Assert.Empty(await _profiles.GetFavoritesAsync(DeviceA, CancellationToken.None));
    }

    [Fact]
    public async Task Device_profiles_round_trip_including_unicode_names()
    {
        var service = CreateService();

        await service.SaveProfileAsync(new DeviceProfile
        {
            DeviceId = DeviceA,
            DisplayName = "هاتف العمل",
            PreferredView = "details",
            SortOrder = "size-desc",
            BenchmarkedConcurrency = 6,
            GallerySources = [KnownPaths.Camera, KnownPaths.Screenshots],
        }, CancellationToken.None);

        var loaded = await service.GetProfileAsync(DeviceA, CancellationToken.None);

        Assert.Equal("هاتف العمل", loaded.DisplayName);
        Assert.Equal("details", loaded.PreferredView);
        Assert.Equal(6, loaded.BenchmarkedConcurrency);
        Assert.Equal(2, loaded.GallerySources.Count);
        Assert.Contains(KnownPaths.Screenshots, loaded.GallerySources);
    }

    [Fact]
    public async Task An_unknown_device_returns_an_empty_profile_rather_than_failing()
    {
        var service = CreateService();

        var profile = await service.GetProfileAsync(new DeviceId("never-seen"), CancellationToken.None);

        Assert.Equal(new DeviceId("never-seen"), profile.DeviceId);
        Assert.Empty(profile.Favorites);
        Assert.Null(profile.DisplayName);
    }

    private sealed class StubShell(string root) : IShellIntegration
    {
        public Task RevealInFileManagerAsync(string localPath) => Task.CompletedTask;

        public Task OpenAsync(string localPath) => Task.CompletedTask;

        public Task OpenWithAsync(string localPath) => Task.CompletedTask;

        public string GetDefaultDownloadFolder() => root;

        public string GetAppDataFolder() => root;
    }
}
