using System.Diagnostics;
using System.Security.Cryptography;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Data;
using AndroidExplorer.Search;
using AndroidExplorer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AndroidExplorer.Adb.Tests;

/// <summary>
/// File operations, transfers and indexing against a real phone (spec §80, §81).
/// </summary>
/// <remarks>
/// <para>
/// Everything here has been verified against <see cref="FakeAdbServer"/>, which is a server this project also
/// wrote — so it can only prove internal consistency. These tests are the ones that can find a wrong
/// assumption about how Android actually behaves.
/// </para>
/// <para>
/// All writes go to a scratch folder under shared storage and are removed afterwards, so a failed run cannot
/// leave rubbish on the user's phone.
/// </para>
/// </remarks>
public sealed class RealDeviceOperationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly string _localRoot = Path.Combine(
        Path.GetTempPath(), $"ae-live-{Guid.NewGuid():N}");

    private ServiceProvider? _provider;
    private IDeviceFileSystem? _fileSystem;
    private DevicePath _scratch;

    private bool Available => _provider is not null;

    public async Task InitializeAsync()
    {
        if (!AdbTestEnvironment.HasOnlineDevice)
        {
            return;
        }

        Directory.CreateDirectory(_localRoot);

        _provider = AdbTestEnvironment.BuildProvider();

        var device = new DeviceId(
            AdbTestEnvironment.CliDevices.First(candidate => candidate.State == "device").Serial);

        _fileSystem = _provider.GetRequiredService<IAdbFileSystemFactory>()
            .Create(device, FakeServerFixture.FullCapabilities);

        // A clearly named scratch folder, so anything left behind by a crash is obvious.
        _scratch = KnownPaths.Download.Combine($"android-explorer-tests-{Guid.NewGuid():N}"[..40]);

        using var cancellation = new CancellationTokenSource(Timeout);
        await _fileSystem.CreateDirectoryAsync(_scratch, cancellation.Token);
    }

    public async Task DisposeAsync()
    {
        if (_fileSystem is not null)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(Timeout);
                await _fileSystem.DeleteAsync(_scratch, recursive: true, cancellation.Token);
            }
            catch (Core.Exceptions.DeviceException)
            {
                // Best effort: never fail a run over cleanup.
            }
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        try
        {
            Directory.Delete(_localRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DeviceId Device => _fileSystem!.DeviceId;

    // ---------------- phase 2: file operations ----------------

    /// <summary>
    /// Creates, renames and deletes on the phone, using names Android allows and Windows does not.
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task File_operations_round_trip_with_awkward_names()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        // Every one of these is legal on Android; several are illegal on Windows (spec §74).
        foreach (var name in new[] { "صور العائلة", "照片 test", "it's mine", "a b  c", "emoji 🌴" })
        {
            var folder = _scratch.Combine(name);

            await _fileSystem!.CreateDirectoryAsync(folder, cancellation.Token);
            Assert.True(await _fileSystem.ExistsAsync(folder, cancellation.Token),
                $"folder '{name}' was not created");

            // The listing must return the name byte-for-byte, not a mangled approximation.
            var entries = await _fileSystem.ListAsync(_scratch, cancellation.Token);
            Assert.Contains(entries, entry => entry.Name == name);

            var renamed = _scratch.Combine(name + " renamed");
            await _fileSystem.RenameAsync(folder, renamed, cancellation.Token);

            Assert.False(await _fileSystem.ExistsAsync(folder, cancellation.Token));
            Assert.True(await _fileSystem.ExistsAsync(renamed, cancellation.Token));

            await _fileSystem.DeleteAsync(renamed, recursive: true, cancellation.Token);
            Assert.False(await _fileSystem.ExistsAsync(renamed, cancellation.Token));
        }
    }

    [RequiresOnlineDeviceFact]
    public async Task Protected_locations_are_refused_with_a_human_message()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        // Spec §17, §78: never claim to browse the whole filesystem, and explain rather than throw noise.
        var exception = await Assert.ThrowsAsync<Core.Exceptions.AccessDeniedException>(
            () => _fileSystem!.ListAsync(DevicePath.Parse("/data/data"), cancellation.Token));

        Assert.Contains("protected", exception.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- phase 3: transfers ----------------

    /// <summary>
    /// Uploads a file, pulls it back, and verifies the bytes survived the round trip.
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task Upload_and_download_round_trip_is_byte_identical()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        var content = new byte[3 * 1024 * 1024];
        RandomNumberGenerator.Fill(content);
        var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var remote = _scratch.Combine("round-trip.bin");

        await using (var source = new MemoryStream(content))
        {
            await _fileSystem!.UploadAsync(source, remote, null, cancellation.Token);
        }

        var info = await _fileSystem!.GetInfoAsync(remote, cancellation.Token);
        Assert.Equal(content.Length, info.Size);

        // The device's own hash must agree, which also exercises the optional verification path (spec §37).
        var deviceHash = await _fileSystem.ComputeSha256Async(remote, cancellation.Token);
        Assert.Equal(expectedHash, deviceHash);

        using var downloaded = new MemoryStream();
        await _fileSystem.DownloadAsync(remote, downloaded, null, cancellation.Token);

        Assert.Equal(content, downloaded.ToArray());
    }

    /// <summary>
    /// Resumes an upload from a 1 MiB-aligned partial, the mechanism behind spec §13.
    /// </summary>
    /// <remarks>
    /// This is the test that proves <c>dd seek=N conv=notrunc</c> behaves as assumed on this OEM's toybox.
    /// The fake server models what the documentation says; only a real phone can confirm it.
    /// </remarks>
    [RequiresOnlineDeviceFact]
    public async Task Resumed_upload_produces_a_byte_identical_file_on_the_device()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        const int total = 3 * 1024 * 1024;
        const int alignedPartial = 2 * 1024 * 1024;

        var content = new byte[total];
        RandomNumberGenerator.Fill(content);

        var remote = _scratch.Combine("resumed.bin");

        // Stand in for an interrupted attempt: the first 2 MiB already on the device.
        await using (var head = new MemoryStream(content, 0, alignedPartial))
        {
            await _fileSystem!.UploadAsync(head, remote, null, cancellation.Token);
        }

        Assert.Equal(alignedPartial,
            (await _fileSystem!.GetInfoAsync(remote, cancellation.Token)).Size);

        // Resume from the aligned boundary.
        await using (var tail = new MemoryStream(content, alignedPartial, total - alignedPartial))
        {
            await _fileSystem.UploadRangeAsync(tail, remote, alignedPartial, null, cancellation.Token);
        }

        var info = await _fileSystem.GetInfoAsync(remote, cancellation.Token);
        Assert.Equal(total, info.Size);

        var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var actualHash = await _fileSystem.ComputeSha256Async(remote, cancellation.Token);

        Assert.Equal(expectedHash, actualHash);
    }

    /// <summary>Resumes a download from an aligned offset, the pull half of spec §13.</summary>
    [RequiresOnlineDeviceFact]
    public async Task Resumed_download_reassembles_the_original_file()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        const int total = 3 * 1024 * 1024;
        const int resumeFrom = 1024 * 1024;

        var content = new byte[total];
        RandomNumberGenerator.Fill(content);

        var remote = _scratch.Combine("pull-resume.bin");

        await using (var source = new MemoryStream(content))
        {
            await _fileSystem!.UploadAsync(source, remote, null, cancellation.Token);
        }

        // The first megabyte, as an interrupted attempt would have left it, plus the resumed remainder.
        using var assembled = new MemoryStream();
        assembled.Write(content, 0, resumeFrom);

        await _fileSystem!.DownloadRangeAsync(remote, resumeFrom, assembled, null, cancellation.Token);

        Assert.Equal(total, assembled.Length);
        Assert.Equal(content, assembled.ToArray());
    }

    [RequiresOnlineDeviceFact]
    public async Task The_transfer_manager_completes_a_real_download()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        var content = new byte[2 * 1024 * 1024];
        RandomNumberGenerator.Fill(content);
        var remote = _scratch.Combine("managed.bin");

        await using (var source = new MemoryStream(content))
        {
            await _fileSystem!.UploadAsync(source, remote, null, cancellation.Token);
        }

        var databasePath = Path.Combine(_localRoot, "jobs.db");
        var database = new AndroidExplorerDatabase(
            databasePath, NullLogger<AndroidExplorerDatabase>.Instance);
        var store = new SqliteTransferJobStore(database, NullLogger<SqliteTransferJobStore>.Instance);

        await using var manager = new TransferManager(
            Device, _fileSystem!, store, new LiveSettings(), NullLogger<TransferManager>.Instance);

        var ids = await manager.EnqueueDownloadAsync(
            [remote], _localRoot, ConflictPolicy.Replace, cancellation.Token);

        var deadline = DateTime.UtcNow + Timeout;
        TransferJob? job = null;

        while (DateTime.UtcNow < deadline)
        {
            job = manager.Jobs.FirstOrDefault(candidate => candidate.Id == ids[0]);
            if (job is not null && job.IsTerminal)
            {
                break;
            }

            await Task.Delay(50, cancellation.Token);
        }

        Assert.NotNull(job);
        Assert.Equal(TransferStatus.Completed, job!.Status);

        var local = Path.Combine(_localRoot, "managed.bin");
        Assert.Equal(content, await File.ReadAllBytesAsync(local, cancellation.Token));

        // The staging file must not survive a successful transfer.
        Assert.False(File.Exists(local + ".aepart"));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Downloads a mixed selection of files and folders, recreating the tree locally (spec §31).
    /// </summary>
    /// <remarks>
    /// The point of the test is the shape of the result, not just the bytes: exporting a folder must produce
    /// the same folders on the PC, not a flattened heap of files whose names may collide.
    /// </remarks>
    [RequiresOnlineDeviceFact]
    public async Task A_mixed_selection_downloads_with_its_folder_structure_intact()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        // Build a nested tree on the device, including a unicode folder name.
        var trip = _scratch.Combine("Trip");
        var day1 = trip.Combine("Day 1");
        var nested = day1.Combine("صور");
        var loose = _scratch.Combine("loose-file.bin");

        foreach (var folder in new[] { trip, day1, nested })
        {
            await _fileSystem!.CreateDirectoryAsync(folder, cancellation.Token);
        }

        var contents = new Dictionary<DevicePath, byte[]>
        {
            [trip.Combine("top.bin")] = RandomNumberGenerator.GetBytes(4096),
            [day1.Combine("inside.bin")] = RandomNumberGenerator.GetBytes(8192),
            [nested.Combine("deep.bin")] = RandomNumberGenerator.GetBytes(2048),
            [loose] = RandomNumberGenerator.GetBytes(1024),
        };

        foreach (var (path, bytes) in contents)
        {
            await using var source = new MemoryStream(bytes);
            await _fileSystem!.UploadAsync(source, path, null, cancellation.Token);
        }

        var databasePath = Path.Combine(_localRoot, "structure.db");
        var database = new AndroidExplorerDatabase(
            databasePath, NullLogger<AndroidExplorerDatabase>.Instance);
        var store = new SqliteTransferJobStore(database, NullLogger<SqliteTransferJobStore>.Instance);

        await using var manager = new TransferManager(
            Device, _fileSystem!, store, new LiveSettings(), NullLogger<TransferManager>.Instance);

        var destination = Path.Combine(_localRoot, "export");
        Directory.CreateDirectory(destination);

        // A folder and a loose file together, as a multi-selection would be.
        var plan = await manager.PlanDownloadAsync(
            [trip, loose], destination, null, cancellation.Token);

        Assert.Equal(4, plan.FileCount);
        Assert.Equal(contents.Values.Sum(bytes => bytes.LongLength), plan.TotalBytes);

        var ids = await manager.EnqueueDownloadAsync(
            [trip, loose], destination, ConflictPolicy.Replace, cancellation.Token);

        Assert.Equal(4, ids.Count);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline
               && manager.Jobs.Count(job => job.Status == TransferStatus.Completed) < ids.Count)
        {
            var failed = manager.Jobs.FirstOrDefault(job => job.Status == TransferStatus.Failed);
            Assert.True(failed is null, $"a transfer failed: {failed?.Error}");

            await Task.Delay(50, cancellation.Token);
        }

        // The tree must be recreated exactly, unicode folder included.
        var expected = new Dictionary<string, byte[]>
        {
            [Path.Combine(destination, "Trip", "top.bin")] = contents[trip.Combine("top.bin")],
            [Path.Combine(destination, "Trip", "Day 1", "inside.bin")] =
                contents[day1.Combine("inside.bin")],
            [Path.Combine(destination, "Trip", "Day 1", "صور", "deep.bin")] =
                contents[nested.Combine("deep.bin")],
            [Path.Combine(destination, "loose-file.bin")] = contents[loose],
        };

        foreach (var (path, bytes) in expected)
        {
            Assert.True(File.Exists(path), $"expected {path} to exist");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, cancellation.Token));
        }

        // Nothing may be flattened into the destination root beyond the loose file itself.
        var strays = Directory.GetFiles(destination)
            .Select(Path.GetFileName)
            .Where(name => name != "loose-file.bin")
            .ToList();

        Assert.True(strays.Count == 0,
            "files were flattened into the destination root: " + string.Join(", ", strays));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>Uploads a local folder tree and checks it is recreated on the device (spec §9).</summary>
    [RequiresOnlineDeviceFact]
    public async Task Uploading_a_folder_recreates_its_structure_on_the_device()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        var local = Path.Combine(_localRoot, "to-upload");
        var localNested = Path.Combine(local, "sub", "deeper");
        Directory.CreateDirectory(localNested);

        var top = RandomNumberGenerator.GetBytes(2048);
        var deep = RandomNumberGenerator.GetBytes(4096);

        await File.WriteAllBytesAsync(Path.Combine(local, "top.bin"), top, cancellation.Token);
        await File.WriteAllBytesAsync(Path.Combine(localNested, "deep.bin"), deep, cancellation.Token);

        var databasePath = Path.Combine(_localRoot, "upload.db");
        var database = new AndroidExplorerDatabase(
            databasePath, NullLogger<AndroidExplorerDatabase>.Instance);
        var store = new SqliteTransferJobStore(database, NullLogger<SqliteTransferJobStore>.Instance);

        await using var manager = new TransferManager(
            Device, _fileSystem!, store, new LiveSettings(), NullLogger<TransferManager>.Instance);

        var ids = await manager.EnqueueUploadAsync(
            [local], _scratch, ConflictPolicy.Replace, cancellation.Token);

        Assert.Equal(2, ids.Count);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline
               && manager.Jobs.Count(job => job.Status == TransferStatus.Completed) < ids.Count)
        {
            var failed = manager.Jobs.FirstOrDefault(job => job.Status == TransferStatus.Failed);
            Assert.True(failed is null, $"an upload failed: {failed?.Error}");

            await Task.Delay(50, cancellation.Token);
        }

        // The device must hold the same shape the PC had.
        var uploadedTop = _scratch.Combine("to-upload").Combine("top.bin");
        var uploadedDeep = _scratch.Combine("to-upload").Combine("sub").Combine("deeper")
            .Combine("deep.bin");

        Assert.True(await _fileSystem!.ExistsAsync(uploadedTop, cancellation.Token));
        Assert.True(await _fileSystem.ExistsAsync(uploadedDeep, cancellation.Token));

        Assert.Equal(deep.Length,
            (await _fileSystem.GetInfoAsync(uploadedDeep, cancellation.Token)).Size);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // ---------------- phase 5: indexing and search ----------------

    /// <summary>
    /// Crawls part of the real device and measures how fast search answers afterwards (spec §28, §45).
    /// </summary>
    [RequiresOnlineDeviceFact]
    public async Task Indexing_real_storage_makes_search_fast()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        var databasePath = Path.Combine(_localRoot, "index.db");
        var database = new AndroidExplorerDatabase(
            databasePath, NullLogger<AndroidExplorerDatabase>.Instance);
        var index = new SqliteFileIndexStore(database, NullLogger<SqliteFileIndexStore>.Instance);

        // Index DCIM rather than the whole phone, to keep the test to a sensible duration.
        var crawled = 0;
        var stopwatch = Stopwatch.StartNew();

        var queue = new Queue<DevicePath>();
        queue.Enqueue(KnownPaths.Dcim);

        while (queue.Count > 0 && crawled < 20_000)
        {
            var current = queue.Dequeue();

            IReadOnlyList<DeviceEntry> entries;
            try
            {
                entries = await _fileSystem!.ListAsync(current, cancellation.Token);
            }
            catch (Core.Exceptions.DeviceException)
            {
                continue;
            }

            foreach (var entry in entries.Where(entry => entry.IsDirectory))
            {
                queue.Enqueue(entry.Path);
            }

            await index.UpsertBatchAsync(Device, entries, cancellation.Token);
            crawled += entries.Count;
        }

        stopwatch.Stop();

        var indexed = await index.CountAsync(Device, cancellation.Token);
        Console.WriteLine(
            $"INDEX: {indexed:N0} entries from DCIM in {stopwatch.Elapsed.TotalSeconds:N1}s");

        if (indexed == 0)
        {
            // An empty DCIM is unusual but not a failure of the code under test.
            return;
        }

        // Search must answer from the index, not the device — the point of having one (spec §28).
        var searchTimer = Stopwatch.StartNew();
        var results = await index.SearchAsync(
            Device, new SearchQuery { Text = "IMG" }, cancellation.Token);
        searchTimer.Stop();

        Console.WriteLine(
            $"SEARCH: {results.Count} matches in {searchTimer.Elapsed.TotalMilliseconds:N0} ms");

        Assert.True(searchTimer.Elapsed.TotalMilliseconds < 1000,
            $"search took {searchTimer.Elapsed.TotalMilliseconds:N0} ms, which is too slow to feel instant");

        // Storage aggregation runs off the same index and must not need the device at all.
        var categories = await index.AggregateByKindAsync(Device, cancellation.Token);
        var (bytes, files) = await index.TotalsAsync(Device, cancellation.Token);

        Console.WriteLine(
            $"STORAGE: {files:N0} files, {bytes / (1024.0 * 1024):N0} MB across {categories.Count} categories");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [RequiresOnlineDeviceFact]
    public async Task Storage_volumes_are_reported()
    {
        if (!Available)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(Timeout);

        var volumes = await _fileSystem!.GetStorageAsync(cancellation.Token);

        Assert.NotEmpty(volumes);

        var internalStorage = volumes.First();
        Assert.True(internalStorage.TotalBytes > 1_000_000_000);
        Assert.InRange(internalStorage.FreeBytes, 0, internalStorage.TotalBytes);

        Console.WriteLine(
            $"STORAGE VOLUME: {internalStorage.UsedBytes / (1024.0 * 1024 * 1024):N1} GB used of "
            + $"{internalStorage.TotalBytes / (1024.0 * 1024 * 1024):N1} GB");
    }

    private sealed class LiveSettings : ISettingsService
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
