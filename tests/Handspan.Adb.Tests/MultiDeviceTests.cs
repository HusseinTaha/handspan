using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Handspan.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Adb.Tests;

/// <summary>
/// Two devices connected at once (spec §39).
/// </summary>
/// <remarks>
/// The failure this guards against is subtle and specific: a cache, queue or index that keys on path alone
/// blends two phones together, so a folder listing or a transfer silently belongs to the wrong device. Two
/// independent fake servers make that observable.
/// </remarks>
public sealed class MultiDeviceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ae-multi-{Guid.NewGuid():N}.db");

    private readonly string _localRoot = Path.Combine(
        Path.GetTempPath(), $"ae-multi-{Guid.NewGuid():N}");

    private FakeServerFixture _phoneA = null!;
    private FakeServerFixture _phoneB = null!;
    private HandspanDatabase _database = null!;

    public Task InitializeAsync()
    {
        _phoneA = FakeServerFixture.Start();
        _phoneB = FakeServerFixture.Start();

        // Distinct serials, as two real phones would have.
        _phoneA.Server.Serial = "PHONE-A-0001";
        _phoneB.Server.Serial = "PHONE-B-0002";

        Directory.CreateDirectory(_localRoot);
        _database = new HandspanDatabase(
            _databasePath, NullLogger<HandspanDatabase>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _phoneA.DisposeAsync();
        await _phoneB.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            TryDelete(_databasePath + suffix);
        }

        try
        {
            Directory.Delete(_localRoot, recursive: true);
        }
        catch (IOException)
        {
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Each_device_lists_its_own_files()
    {
        _phoneA.Server.Files.AddFile("/storage/emulated/0/Download/only-on-a.txt", "a");
        _phoneB.Server.Files.AddFile("/storage/emulated/0/Download/only-on-b.txt", "b");

        var listingA = await _phoneA.CreateFileSystem()
            .ListAsync(KnownPaths.Download, CancellationToken.None);
        var listingB = await _phoneB.CreateFileSystem()
            .ListAsync(KnownPaths.Download, CancellationToken.None);

        Assert.Contains(listingA, entry => entry.Name == "only-on-a.txt");
        Assert.DoesNotContain(listingA, entry => entry.Name == "only-on-b.txt");

        Assert.Contains(listingB, entry => entry.Name == "only-on-b.txt");
        Assert.DoesNotContain(listingB, entry => entry.Name == "only-on-a.txt");
    }

    [Fact]
    public async Task The_directory_cache_does_not_blend_two_devices()
    {
        var cache = new SqliteCacheService(_database, NullLogger<SqliteCacheService>.Instance);

        var fileSystemA = new CachedDeviceFileSystem(_phoneA.CreateFileSystem(), cache);
        var fileSystemB = new CachedDeviceFileSystem(_phoneB.CreateFileSystem(), cache);

        _phoneA.Server.Files.AddFile("/storage/emulated/0/DCIM/photo-a.jpg", "a");
        _phoneB.Server.Files.AddFile("/storage/emulated/0/DCIM/photo-b.jpg", "b");

        // Same path on both devices — the exact collision §39 exists to prevent.
        await fileSystemA.ListAsync(KnownPaths.Dcim, CancellationToken.None);
        await fileSystemB.ListAsync(KnownPaths.Dcim, CancellationToken.None);

        var cachedA = await cache.GetListingAsync(
            _phoneA.Device, KnownPaths.Dcim, CancellationToken.None);
        var cachedB = await cache.GetListingAsync(
            _phoneB.Device, KnownPaths.Dcim, CancellationToken.None);

        Assert.NotNull(cachedA);
        Assert.NotNull(cachedB);

        Assert.Contains(cachedA!, entry => entry.Name == "photo-a.jpg");
        Assert.DoesNotContain(cachedA!, entry => entry.Name == "photo-b.jpg");
        Assert.Contains(cachedB!, entry => entry.Name == "photo-b.jpg");
        Assert.DoesNotContain(cachedB!, entry => entry.Name == "photo-a.jpg");
    }

    [Fact]
    public async Task A_write_on_one_device_does_not_invalidate_the_others_cache()
    {
        var cache = new SqliteCacheService(_database, NullLogger<SqliteCacheService>.Instance);

        var fileSystemA = new CachedDeviceFileSystem(_phoneA.CreateFileSystem(), cache);
        var fileSystemB = new CachedDeviceFileSystem(_phoneB.CreateFileSystem(), cache);

        await fileSystemA.ListAsync(KnownPaths.Download, CancellationToken.None);
        await fileSystemB.ListAsync(KnownPaths.Download, CancellationToken.None);

        // Creating a folder on A must invalidate A's listing and leave B's intact.
        await fileSystemA.CreateDirectoryAsync(
            KnownPaths.Download.Combine("new-folder"), CancellationToken.None);

        Assert.Null(await cache.GetListingAsync(
            _phoneA.Device, KnownPaths.Download, CancellationToken.None));
        Assert.NotNull(await cache.GetListingAsync(
            _phoneB.Device, KnownPaths.Download, CancellationToken.None));
    }

    [Fact]
    public async Task Transfers_run_independently_on_two_devices()
    {
        var store = new SqliteTransferJobStore(
            _database, NullLogger<SqliteTransferJobStore>.Instance);
        var settings = new StubSettings();

        var expectedA = _phoneA.Server.Files.AddGeneratedFile(
            "/storage/emulated/0/Download/from-a.bin", 200_000);
        var expectedB = _phoneB.Server.Files.AddGeneratedFile(
            "/storage/emulated/0/Download/from-b.bin", 300_000);

        await using var managerA = new TransferManager(
            _phoneA.Device, _phoneA.CreateFileSystem(), store, settings,
            NullLogger<TransferManager>.Instance);
        await using var managerB = new TransferManager(
            _phoneB.Device, _phoneB.CreateFileSystem(), store, settings,
            NullLogger<TransferManager>.Instance);

        var folderA = Path.Combine(_localRoot, "a");
        var folderB = Path.Combine(_localRoot, "b");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);

        // Queue on both at once: a background device must keep transferring while another is in use.
        var idsA = await managerA.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("from-a.bin")], folderA, ConflictPolicy.Replace,
            CancellationToken.None);
        var idsB = await managerB.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("from-b.bin")], folderB, ConflictPolicy.Replace,
            CancellationToken.None);

        await WaitForCompletionAsync(managerA, idsA[0]);
        await WaitForCompletionAsync(managerB, idsB[0]);

        Assert.Equal(expectedA, await File.ReadAllBytesAsync(Path.Combine(folderA, "from-a.bin")));
        Assert.Equal(expectedB, await File.ReadAllBytesAsync(Path.Combine(folderB, "from-b.bin")));

        // Each manager sees only its own device's jobs.
        Assert.Single(managerA.Jobs);
        Assert.Single(managerB.Jobs);
        Assert.All(managerA.Jobs, job => Assert.Equal(_phoneA.Device, job.DeviceId));
        Assert.All(managerB.Jobs, job => Assert.Equal(_phoneB.Device, job.DeviceId));
    }

    [Fact]
    public async Task Pausing_one_device_leaves_the_other_running()
    {
        var store = new SqliteTransferJobStore(
            _database, NullLogger<SqliteTransferJobStore>.Instance);
        var settings = new StubSettings();

        _phoneA.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/a.bin", 100_000);
        _phoneB.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/b.bin", 100_000);

        await using var managerA = new TransferManager(
            _phoneA.Device, _phoneA.CreateFileSystem(), store, settings,
            NullLogger<TransferManager>.Instance);
        await using var managerB = new TransferManager(
            _phoneB.Device, _phoneB.CreateFileSystem(), store, settings,
            NullLogger<TransferManager>.Instance);

        var idsA = await managerA.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("a.bin")], _localRoot, ConflictPolicy.Rename,
            CancellationToken.None);

        // Disconnecting A pauses only A's queue (spec §38).
        await managerA.PauseAllAsync("Device A was disconnected.");

        var idsB = await managerB.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("b.bin")], _localRoot, ConflictPolicy.Rename,
            CancellationToken.None);

        await WaitForCompletionAsync(managerB, idsB[0]);

        Assert.Equal(TransferStatus.Completed, managerB.Jobs.Single().Status);
        Assert.NotEqual(TransferStatus.Transferring, managerA.Jobs.Single(j => j.Id == idsA[0]).Status);
    }

    [Fact]
    public async Task The_journal_restores_only_the_requested_devices_jobs()
    {
        var store = new SqliteTransferJobStore(
            _database, NullLogger<SqliteTransferJobStore>.Instance);

        foreach (var (device, name) in new[] { (_phoneA.Device, "a.bin"), (_phoneB.Device, "b.bin") })
        {
            await store.SaveAsync(new TransferJob
            {
                Id = Guid.NewGuid(),
                DeviceId = device,
                Direction = TransferDirection.Download,
                RemotePath = KnownPaths.Download.Combine(name),
                LocalPath = Path.Combine(_localRoot, name),
                TotalBytes = 1000,
                Status = TransferStatus.Paused,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var restoredA = await store.LoadAsync(_phoneA.Device, CancellationToken.None);
        var restoredB = await store.LoadAsync(_phoneB.Device, CancellationToken.None);

        Assert.Equal("a.bin", Assert.Single(restoredA).RemotePath.Name);
        Assert.Equal("b.bin", Assert.Single(restoredB).RemotePath.Name);
    }

    private static async Task WaitForCompletionAsync(ITransferManager manager, Guid id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var job = manager.Jobs.FirstOrDefault(candidate => candidate.Id == id);
            if (job is { Status: TransferStatus.Completed })
            {
                return;
            }

            if (job is { Status: TransferStatus.Failed })
            {
                throw new InvalidOperationException($"transfer failed: {job.Error}");
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("the transfer did not finish");
    }

    private sealed class StubSettings : ISettingsService
    {
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
            => Task.FromResult(new DeviceProfile { DeviceId = deviceId });

        public Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
