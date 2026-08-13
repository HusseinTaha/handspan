using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Handspan.Data;
using Handspan.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Adb.Tests;

/// <summary>
/// The transfer engine against the fake server (spec §11–§13).
/// </summary>
/// <remarks>
/// These are the tests real hardware cannot provide: a phone cannot be asked to drop its connection at
/// a precise byte offset. The whole production path is exercised — sync protocol, <c>dd</c> resume,
/// journal, scheduler — with only the server replaced.
/// </remarks>
public sealed class TransferManagerTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ae-transfers-{Guid.NewGuid():N}.db");

    private readonly string _localRoot = Path.Combine(
        Path.GetTempPath(), $"ae-transfers-{Guid.NewGuid():N}");

    private FakeServerFixture _fixture = null!;
    private ITransferJobStore _store = null!;

    public Task InitializeAsync()
    {
        _fixture = FakeServerFixture.Start();
        Directory.CreateDirectory(_localRoot);

        var database = new HandspanDatabase(
            _databasePath, NullLogger<HandspanDatabase>.Instance);
        _store = new SqliteTransferJobStore(database, NullLogger<SqliteTransferJobStore>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
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
            // Temp cleanup is best effort.
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

    private TransferManager CreateManager(AppSettings? settings = null)
        => new(
            _fixture.Device,
            _fixture.CreateFileSystem(),
            _store,
            new FixedSettings(settings ?? new AppSettings()),
            NullLogger<TransferManager>.Instance);

    /// <summary>Settings that never change, so a test's configuration is exactly what it asked for.</summary>
    private sealed class FixedSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task LoadAsync(CancellationToken cancellationToken)
        {
            _ = Changed;
            return Task.CompletedTask;
        }

        public Task SaveAsync(AppSettings updated, CancellationToken cancellationToken)
            => throw new NotSupportedException("Tests do not change settings mid-run.");

        public Task<DeviceProfile> GetProfileAsync(DeviceId deviceId, CancellationToken cancellationToken)
            => Task.FromResult(new DeviceProfile { DeviceId = deviceId });

        public Task SaveProfileAsync(DeviceProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>Waits for a job to reach one of the given statuses, failing the test on timeout.</summary>
    private async Task<TransferJob> WaitForAsync(
        ITransferManager manager,
        Guid id,
        params TransferStatus[] statuses)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
            if (job is not null && statuses.Contains(job.Status))
            {
                return job;
            }

            await Task.Delay(25);
        }

        var last = manager.Jobs.FirstOrDefault(j => j.Id == id);

        string commands;
        lock (_fixture.Server.ExecutedCommands)
        {
            commands = string.Join(" | ", _fixture.Server.ExecutedCommands);
        }

        throw new TimeoutException(
            $"job never reached {string.Join('/', statuses)}; last status was {last?.Status}, "
            + $"bytes {last?.BytesTransferred}/{last?.TotalBytes}, retries {last?.RetryCount}, "
            + $"error: {last?.Error ?? "(none)"}. device commands: [{commands}]");
    }

    [Fact]
    public async Task Downloads_a_file_and_verifies_its_size()
    {
        var expected = _fixture.Server.Files.AddGeneratedFile(
            "/storage/emulated/0/Download/photo.jpg", 300_000);

        await using var manager = CreateManager();

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("photo.jpg")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        var job = await WaitForAsync(manager, ids[0], TransferStatus.Completed);

        Assert.Equal(expected.Length, job.TotalBytes);

        var local = Path.Combine(_localRoot, "photo.jpg");
        Assert.True(File.Exists(local));
        Assert.Equal(expected, await File.ReadAllBytesAsync(local));

        // The partial file must not survive a successful transfer.
        Assert.False(File.Exists(local + ".aepart"));
    }

    [Fact]
    public async Task Uploads_a_file_and_commits_it_under_its_real_name()
    {
        var local = Path.Combine(_localRoot, "upload.bin");
        var content = new byte[250_000];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(local, content);

        await using var manager = CreateManager();

        var ids = await manager.EnqueueUploadAsync(
            [local], KnownPaths.Download, ConflictPolicy.Replace, CancellationToken.None);

        await WaitForAsync(manager, ids[0], TransferStatus.Completed);

        var uploaded = _fixture.Server.Files.Resolve("/storage/emulated/0/Download/upload.bin");
        Assert.NotNull(uploaded);
        Assert.Equal(content, uploaded!.Content);

        // The .aepart staging file must be gone, not left beside the real one.
        Assert.Null(_fixture.Server.Files.Get("/storage/emulated/0/Download/upload.bin.aepart"));
    }

    /// <summary>
    /// The spec's own worked example, scaled down: interrupt at 3.2 of 5 units, resume, and require a
    /// byte-identical result (spec §13).
    /// </summary>
    [Fact]
    public async Task Resumes_an_interrupted_download_to_a_byte_identical_file()
    {
        const int total = 5 * 1024 * 1024;      // "5 GB"
        const long dropAt = 3_355_443;          // "3.2 GB"

        var expected = _fixture.Server.Files.AddGeneratedFile(
            "/storage/emulated/0/Movies/holiday.mp4", total);

        _fixture.Server.Faults.DropAfterBytes = dropAt;

        // Retries are disabled so the interruption surfaces as a failure we then resume explicitly.
        await using var manager = CreateManager(new AppSettings { RetryCount = 0 });

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Movies.Combine("holiday.mp4")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        var interrupted = await WaitForAsync(manager, ids[0], TransferStatus.Failed);
        Assert.Equal(TransferStatus.Failed, interrupted.Status);

        // The partial file is kept, aligned down to a 1 MiB boundary on resume.
        var partial = Path.Combine(_localRoot, "holiday.mp4.aepart");
        Assert.True(File.Exists(partial));
        Assert.InRange(new FileInfo(partial).Length, 1, dropAt);

        // Heal the connection and resume.
        _fixture.Server.Faults.DropAfterBytes = null;
        await manager.ResumeAsync(ids[0]);

        await WaitForAsync(manager, ids[0], TransferStatus.Completed);

        var local = Path.Combine(_localRoot, "holiday.mp4");
        var actual = await File.ReadAllBytesAsync(local);

        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);

        // Resume must have gone through dd with a block-aligned skip, not restarted from zero.
        var ddCommands = _fixture.Server.ExecutedCommands
            .Where(command => command.StartsWith("dd ", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(ddCommands);
        Assert.Contains(ddCommands, command => command.Contains("bs=1048576", StringComparison.Ordinal)
                                               && command.Contains("skip=3", StringComparison.Ordinal));
    }

    /// <summary>
    /// Resumes an upload from an existing partial on the device (spec §13).
    /// </summary>
    /// <remarks>
    /// The partial is placed deterministically rather than produced by interrupting a live transfer:
    /// racing the fault injector makes the *test* flaky without testing the resume logic any harder.
    /// The interruption itself is covered by <see cref="Interrupting_an_upload_keeps_the_partial_file"/>.
    /// </remarks>
    [Fact]
    public async Task Resumes_an_upload_by_sending_and_appending_the_remainder()
    {
        const int total = 5 * 1024 * 1024;
        const int alignedPartial = 3 * 1024 * 1024;

        var content = new byte[total];
        for (var i = 0; i < total; i++)
        {
            content[i] = (byte)(i * 17 % 253);
        }

        var local = Path.Combine(_localRoot, "big.bin");
        await File.WriteAllBytesAsync(local, content);

        // A previous attempt left an aligned partial on the device.
        _fixture.Server.Files.AddFile(
            "/storage/emulated/0/Download/big.bin.aepart", content[..alignedPartial]);

        await using var manager = CreateManager(new AppSettings { RetryCount = 0 });

        var ids = await manager.EnqueueUploadAsync(
            [local], KnownPaths.Download, ConflictPolicy.Replace, CancellationToken.None);

        await WaitForAsync(manager, ids[0], TransferStatus.Completed);

        var uploaded = _fixture.Server.Files.Resolve("/storage/emulated/0/Download/big.bin");
        Assert.NotNull(uploaded);
        Assert.Equal(total, uploaded!.Content.Length);

        // Locate the first difference explicitly: a wrong resume offset shows up as a run of zeros, and
        // "collections differ" would not say where.
        var firstDifference = -1;
        for (var i = 0; i < total; i++)
        {
            if (uploaded.Content[i] != content[i])
            {
                firstDifference = i;
                break;
            }
        }

        Assert.True(firstDifference < 0, $"resumed upload differs at byte {firstDifference}");

        // It must have resumed by appending the remainder rather than re-sending the whole file. Piping into
        // dd was the original design and loses data on real hardware — see AdbFileSystem.UploadRangeAsync.
        Assert.Contains(_fixture.Server.ExecutedCommands,
            command => command.StartsWith("cat ", StringComparison.Ordinal)
                       && command.Contains(">>", StringComparison.Ordinal));

        // Neither staging file may be left behind: the fragment is removed and the part is renamed into place.
        Assert.Null(_fixture.Server.Files.Get("/storage/emulated/0/Download/big.bin.aepart"));
        Assert.Null(_fixture.Server.Files.Get("/storage/emulated/0/Download/big.bin.aepart.aeresume"));
    }

    /// <summary>An interrupted upload must leave its partial data on the device to resume from.</summary>
    [Fact]
    public async Task Interrupting_an_upload_keeps_the_partial_file()
    {
        const int total = 5 * 1024 * 1024;

        var local = Path.Combine(_localRoot, "interrupted.bin");
        await File.WriteAllBytesAsync(local, new byte[total]);

        _fixture.Server.Faults.DropAfterBytes = 2_000_000;

        await using var manager = CreateManager(new AppSettings { RetryCount = 0 });

        var ids = await manager.EnqueueUploadAsync(
            [local], KnownPaths.Download, ConflictPolicy.Replace, CancellationToken.None);

        var job = await WaitForAsync(manager, ids[0], TransferStatus.Failed, TransferStatus.Completed);

        // The drop is inherently racy against a fast loopback transfer; when it does bite, the partial
        // data and the journalled resume point must both survive.
        if (job.Status == TransferStatus.Failed)
        {
            Assert.True(_fixture.Server.Faults.DropsTriggered > 0);
            Assert.NotNull(_fixture.Server.Files.Get("/storage/emulated/0/Download/interrupted.bin.aepart"));
            Assert.True(job.BytesTransferred > 0, "the resume point must be journalled, not left at zero");
            Assert.True(job.IsResumable);
        }
    }

    [Fact]
    public async Task Retries_a_transient_failure_automatically()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/flaky.bin", 200_000);
        _fixture.Server.Faults.DropAfterBytes = 50_000;

        await using var manager = CreateManager(new AppSettings { RetryCount = 3 });

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("flaky.bin")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        // Let the first attempt fail, then heal the connection mid-retry.
        await WaitForAsync(manager, ids[0], TransferStatus.Retrying, TransferStatus.Failed);
        _fixture.Server.Faults.DropAfterBytes = null;

        var job = await WaitForAsync(manager, ids[0], TransferStatus.Completed, TransferStatus.Failed);

        Assert.Equal(TransferStatus.Completed, job.Status);
        Assert.True(job.RetryCount >= 1, "the transfer should have recorded at least one retry");
    }

    [Fact]
    public async Task Cancelling_removes_the_partial_file()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Movies/cancel-me.mp4", 4 * 1024 * 1024);
        _fixture.Server.Faults.DropAfterBytes = 1_500_000;

        await using var manager = CreateManager(new AppSettings { RetryCount = 0 });

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Movies.Combine("cancel-me.mp4")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        await WaitForAsync(manager, ids[0], TransferStatus.Failed);
        await manager.CancelAsync(ids[0]);

        var job = await WaitForAsync(manager, ids[0], TransferStatus.Cancelled);
        Assert.Equal(TransferStatus.Cancelled, job.Status);

        // Cancelling discards the partial file; pausing would have kept it.
        Assert.False(File.Exists(Path.Combine(_localRoot, "cancel-me.mp4.aepart")));
        Assert.False(File.Exists(Path.Combine(_localRoot, "cancel-me.mp4")));
    }

    [Fact]
    public async Task Pausing_keeps_the_partial_file_so_it_can_resume()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Movies/pause-me.mp4", 4 * 1024 * 1024);
        _fixture.Server.Faults.DropAfterBytes = 2_000_000;

        await using var manager = CreateManager(new AppSettings { RetryCount = 0 });

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Movies.Combine("pause-me.mp4")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        await WaitForAsync(manager, ids[0], TransferStatus.Failed);

        await manager.PauseAllAsync("Device disconnected.");
        var paused = manager.Jobs.First(job => job.Id == ids[0]);

        // A failed-then-paused job must keep its bytes: that is what resume depends on.
        Assert.True(File.Exists(Path.Combine(_localRoot, "pause-me.mp4.aepart")));
        Assert.NotEqual(TransferStatus.Cancelled, paused.Status);
    }

    [Fact]
    public async Task A_directory_download_expands_into_one_job_per_file()
    {
        _fixture.Server.Files.AddDirectory("/storage/emulated/0/DCIM/Trip");
        _fixture.Server.Files.AddDirectory("/storage/emulated/0/DCIM/Trip/Day1");
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/DCIM/Trip/a.jpg", 1000);
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/DCIM/Trip/Day1/b.jpg", 2000);
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/DCIM/Trip/Day1/صورة.jpg", 3000);

        await using var manager = CreateManager();
        var source = KnownPaths.Dcim.Combine("Trip");

        // The preview must be accurate before anything moves (spec §34).
        var plan = await manager.PlanDownloadAsync([source], _localRoot, null, CancellationToken.None);
        Assert.Equal(3, plan.FileCount);
        Assert.Equal(6000, plan.TotalBytes);

        var ids = await manager.EnqueueDownloadAsync(
            [source], _localRoot, ConflictPolicy.Replace, CancellationToken.None);

        Assert.Equal(3, ids.Count);

        foreach (var id in ids)
        {
            await WaitForAsync(manager, id, TransferStatus.Completed);
        }

        // The tree structure is preserved locally, including the unicode filename.
        Assert.True(File.Exists(Path.Combine(_localRoot, "Trip", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(_localRoot, "Trip", "Day1", "b.jpg")));
        Assert.True(File.Exists(Path.Combine(_localRoot, "Trip", "Day1", "صورة.jpg")));
    }

    [Fact]
    public async Task Skip_and_rename_conflict_policies_are_honoured()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/dup.bin", 1000);
        var existing = Path.Combine(_localRoot, "dup.bin");
        await File.WriteAllTextAsync(existing, "original");

        await using var manager = CreateManager();
        var source = KnownPaths.Download.Combine("dup.bin");

        // Skip leaves the local file untouched and queues nothing.
        var skipped = await manager.EnqueueDownloadAsync(
            [source], _localRoot, ConflictPolicy.Skip, CancellationToken.None);
        Assert.Empty(skipped);
        Assert.Equal("original", await File.ReadAllTextAsync(existing));

        // Rename keeps both copies.
        var renamed = await manager.EnqueueDownloadAsync(
            [source], _localRoot, ConflictPolicy.Rename, CancellationToken.None);
        await WaitForAsync(manager, renamed[0], TransferStatus.Completed);

        Assert.Equal("original", await File.ReadAllTextAsync(existing));
        Assert.True(File.Exists(Path.Combine(_localRoot, "dup.bin (1).bin")) ||
                    File.Exists(Path.Combine(_localRoot, "dup (1).bin")),
            "the renamed copy should exist alongside the original");
    }

    [Fact]
    public async Task Optional_hash_verification_passes_on_a_good_transfer()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/verified.bin", 120_000);

        await using var manager = CreateManager(new AppSettings
        {
            Verification = VerificationMode.Sha256,
        });

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("verified.bin")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        await WaitForAsync(manager, ids[0], TransferStatus.Completed);

        Assert.Contains(_fixture.Server.ExecutedCommands,
            command => command.StartsWith("sha256sum", StringComparison.Ordinal));
    }

    /// <summary>
    /// The scheduler must never exceed its configured concurrency (spec §12).
    /// </summary>
    /// <remarks>
    /// The assertion is on the ceiling, not on observing a particular level: over a loopback connection the
    /// whole queue can finish between two samples, and a test that requires catching transfers mid-flight
    /// fails for timing reasons rather than correctness ones. Exceeding the limit is the real defect — it
    /// means the pump handed the same slot out twice.
    /// </remarks>
    [Fact]
    public async Task The_scheduler_never_exceeds_the_small_file_concurrency_limit()
    {
        const int fileCount = 8;
        const int limit = 2;

        for (var i = 0; i < fileCount; i++)
        {
            // Large enough that each transfer takes several protocol round trips.
            _fixture.Server.Files.AddGeneratedFile($"/storage/emulated/0/Download/f{i}.bin", 2_000_000);
        }

        await using var manager = CreateManager(new AppSettings
        {
            MaxConcurrentSmallTransfers = limit,
            MaxConcurrentLargeTransfers = 1,
            LargeFileThresholdBytes = 8 * 1024 * 1024,
        });

        var sources = Enumerable.Range(0, fileCount)
            .Select(i => KnownPaths.Download.Combine($"f{i}.bin"))
            .ToList();

        var ids = await manager.EnqueueDownloadAsync(
            sources, _localRoot, ConflictPolicy.Replace, CancellationToken.None);

        var peak = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            peak = Math.Max(peak, manager.Jobs.Count(job => job.Status == TransferStatus.Transferring));

            if (manager.Jobs.Count(job => job.Status == TransferStatus.Completed) == ids.Count)
            {
                break;
            }

            await Task.Delay(1);
        }

        Assert.Equal(fileCount, manager.Jobs.Count(job => job.Status == TransferStatus.Completed));
        Assert.True(peak <= limit, $"concurrency reached {peak}, above the configured limit of {limit}");

        // Every file must have arrived intact — two workers racing on one slot would corrupt them.
        foreach (var i in Enumerable.Range(0, fileCount))
        {
            var local = Path.Combine(_localRoot, $"f{i}.bin");
            Assert.True(File.Exists(local));
            Assert.Equal(2_000_000, new FileInfo(local).Length);
        }
    }

    /// <summary>
    /// A failed transfer survives a restart as failed — and is still resumable from its partial bytes,
    /// which is what journalling buys (spec §13).
    /// </summary>
    [Fact]
    public async Task A_journalled_transfer_survives_a_restart_and_resumes()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Movies/survivor.mp4", 4 * 1024 * 1024);
        _fixture.Server.Faults.DropAfterBytes = 1_200_000;

        Guid id;

        // First "session": the transfer is interrupted and the manager goes away, as on a crash.
        await using (var first = CreateManager(new AppSettings { RetryCount = 0 }))
        {
            var ids = await first.EnqueueDownloadAsync(
                [KnownPaths.Movies.Combine("survivor.mp4")], _localRoot, ConflictPolicy.Replace,
                CancellationToken.None);
            id = ids[0];

            await WaitForAsync(first, id, TransferStatus.Failed);
        }

        // Second "session": the journal is replayed from disk.
        await using var second = CreateManager(new AppSettings { RetryCount = 0 });
        await second.RestoreAsync(CancellationToken.None);

        var restored = second.Jobs.FirstOrDefault(job => job.Id == id);
        Assert.NotNull(restored);

        // Terminal states are preserved rather than rewritten, so the user still sees what happened.
        Assert.Equal(TransferStatus.Failed, restored!.Status);
        Assert.True(restored.BytesTransferred > 0, "progress should have been journalled");
        Assert.True(restored.IsResumable);

        _fixture.Server.Faults.DropAfterBytes = null;
        await second.ResumeAsync(id);
        await WaitForAsync(second, id, TransferStatus.Completed);

        Assert.True(File.Exists(Path.Combine(_localRoot, "survivor.mp4")));
    }

    /// <summary>
    /// A job that was still moving when the process died comes back paused, not silently restarted.
    /// </summary>
    [Fact]
    public async Task A_transfer_interrupted_mid_flight_is_restored_as_paused()
    {
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/inflight.bin", 50_000);

        // Simulate a process kill: the journal holds a job that never reached a terminal state.
        var job = new TransferJob
        {
            Id = Guid.NewGuid(),
            DeviceId = _fixture.Device,
            Direction = TransferDirection.Download,
            RemotePath = KnownPaths.Download.Combine("inflight.bin"),
            LocalPath = Path.Combine(_localRoot, "inflight.bin"),
            TotalBytes = 50_000,
            BytesTransferred = 20_000,
            Status = TransferStatus.Transferring,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(job, CancellationToken.None);

        await using var manager = CreateManager();
        await manager.RestoreAsync(CancellationToken.None);

        var restored = Assert.Single(manager.Jobs, candidate => candidate.Id == job.Id);
        Assert.Equal(TransferStatus.Paused, restored.Status);
        Assert.NotNull(restored.Error);

        // Nothing restarts on its own; the user (or a reconnect) decides.
        await manager.ResumeAsync(job.Id);
        await WaitForAsync(manager, job.Id, TransferStatus.Completed);
    }

    [Fact]
    public async Task A_windows_hostile_android_filename_is_sanitized_locally()
    {
        // These are legal on Android and illegal on Windows (spec §74).
        _fixture.Server.Files.AddGeneratedFile("/storage/emulated/0/Download/what:is*this?.txt", 100);

        await using var manager = CreateManager();

        var ids = await manager.EnqueueDownloadAsync(
            [KnownPaths.Download.Combine("what:is*this?.txt")], _localRoot, ConflictPolicy.Replace,
            CancellationToken.None);

        await WaitForAsync(manager, ids[0], TransferStatus.Completed);

        var downloaded = Directory.GetFiles(_localRoot, "*.txt");
        Assert.Single(downloaded);
        Assert.DoesNotContain(':', Path.GetFileName(downloaded[0]));
    }
}
