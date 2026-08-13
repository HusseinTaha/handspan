using Handspan.Core.Exceptions;
using Handspan.Core.Models;

namespace Handspan.Adb.Tests;

/// <summary>
/// The transport against a controllable server: the cases real hardware cannot reproduce on demand.
/// </summary>
public class FakeServerProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource Cancellation() => new(Timeout);

    [Fact]
    public async Task Lists_a_directory_and_filters_protocol_noise()
    {
        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Files.AddFile("/storage/emulated/0/DCIM/Camera/IMG_0001.jpg", "photo");
        fixture.Server.Files.AddDirectory("/storage/emulated/0/DCIM/Camera/Sub");

        using var cancellation = Cancellation();
        var entries = await fixture.CreateFileSystem()
            .ListAsync(KnownPaths.Camera, cancellation.Token);

        // "." and ".." are sent by the server and must never reach the UI.
        Assert.DoesNotContain(entries, entry => entry.Name is "." or "..");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Name == "IMG_0001.jpg" && !entry.IsDirectory);
        Assert.Contains(entries, entry => entry.Name == "Sub" && entry.IsDirectory);
    }

    [Theory]
    [InlineData("صور العائلة.jpg")]
    [InlineData("照片.png")]
    [InlineData("旅行 🌴.mp4")]
    [InlineData("it's mine.jpg")]
    [InlineData("back\\slash.jpg")]
    [InlineData("dollar$sign.jpg")]
    [InlineData("semi;colon.jpg")]
    public async Task Round_trips_awkward_filenames_through_the_protocol(string name)
    {
        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Files.AddFile($"/storage/emulated/0/DCIM/Camera/{name}", "content");

        using var cancellation = Cancellation();
        var fileSystem = fixture.CreateFileSystem();

        var entries = await fileSystem.ListAsync(KnownPaths.Camera, cancellation.Token);
        var entry = Assert.Single(entries);
        Assert.Equal(name, entry.Name);

        // Stat goes through a different code path, and delete goes through a quoted shell command —
        // both must handle the same name.
        var info = await fileSystem.GetInfoAsync(entry.Path, cancellation.Token);
        Assert.Equal(7, info.Size);

        await fileSystem.DeleteAsync(entry.Path, recursive: false, cancellation.Token);
        Assert.False(await fileSystem.ExistsAsync(entry.Path, cancellation.Token));
    }

    [Fact]
    public async Task Reports_sizes_above_four_gigabytes_via_stat_v2()
    {
        const long fiveGigabytes = 5L * 1024 * 1024 * 1024;

        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Files.AddSparseFile("/storage/emulated/0/Movies/big.mp4", fiveGigabytes);

        using var cancellation = Cancellation();
        var info = await fixture.CreateFileSystem()
            .GetInfoAsync(KnownPaths.Movies.Combine("big.mp4"), cancellation.Token);

        // Only stat_v2 carries a 64-bit size; this is the path that makes large files honest.
        Assert.True(info.IsSizeKnown);
        Assert.Equal(fiveGigabytes, info.Size);
    }

    [Fact]
    public async Task Without_stat_v2_a_huge_size_is_reported_as_unknown_rather_than_truncated()
    {
        const long fiveGigabytes = 5L * 1024 * 1024 * 1024;

        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Features.Remove("stat_v2");
        fixture.Server.Features.Remove("ls_v2");
        fixture.Server.Files.AddSparseFile("/storage/emulated/0/Movies/big.mp4", fiveGigabytes);

        using var cancellation = Cancellation();
        var capabilities = FakeServerFixture.FullCapabilities with { HasStatV2 = false, HasLsV2 = false };

        var entries = await fixture.CreateFileSystem(capabilities)
            .ListAsync(KnownPaths.Movies, cancellation.Token);

        // A 32-bit field saturates; showing 4 GiB as fact would be a lie, so it is flagged unknown.
        var entry = Assert.Single(entries);
        Assert.False(entry.IsSizeKnown);
    }

    [Fact]
    public async Task Sdcard_symlink_resolves_to_a_directory()
    {
        await using var fixture = FakeServerFixture.Start();
        using var cancellation = Cancellation();

        var info = await fixture.CreateFileSystem()
            .GetInfoAsync(KnownPaths.InternalStorage, cancellation.Token);

        Assert.True(info.IsDirectory);
    }

    [Fact]
    public async Task Missing_paths_raise_not_found()
    {
        await using var fixture = FakeServerFixture.Start();
        using var cancellation = Cancellation();
        var fileSystem = fixture.CreateFileSystem();

        var missing = KnownPaths.Camera.Combine("nope.jpg");

        Assert.False(await fileSystem.ExistsAsync(missing, cancellation.Token));
        await Assert.ThrowsAsync<PathNotFoundException>(
            () => fileSystem.GetInfoAsync(missing, cancellation.Token));
    }

    [Fact]
    public async Task Permission_failures_become_access_denied()
    {
        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Faults.FailingPaths["/storage/emulated/0/Private"] = "permission denied";
        fixture.Server.Files.AddDirectory("/storage/emulated/0/Private");

        using var cancellation = Cancellation();

        var exception = await Assert.ThrowsAsync<AccessDeniedException>(
            () => fixture.CreateFileSystem().ListAsync(
                KnownPaths.InternalStorage.Combine("Private"), cancellation.Token));

        Assert.Contains("protected by Android", exception.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protected_areas_are_refused_before_reaching_the_device()
    {
        await using var fixture = FakeServerFixture.Start();
        using var cancellation = Cancellation();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => fixture.CreateFileSystem().ListAsync(
                DevicePath.Parse("/data/data"), cancellation.Token));

        // Nothing was even asked of the device (spec §17).
        Assert.Empty(fixture.Server.ExecutedCommands);
    }

    [Fact]
    public async Task An_unauthorized_device_reports_the_authorization_prompt()
    {
        await using var fixture = FakeServerFixture.Start();
        await fixture.Server.SetDeviceStateAsync("unauthorized");

        using var cancellation = Cancellation();

        await Assert.ThrowsAsync<DeviceUnauthorizedException>(
            () => fixture.CreateFileSystem().ListAsync(KnownPaths.InternalStorage, cancellation.Token));
    }

    [Fact]
    public async Task A_vanished_device_reports_disconnection()
    {
        await using var fixture = FakeServerFixture.Start();
        await fixture.Server.SetDeviceStateAsync("absent");

        using var cancellation = Cancellation();

        await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => fixture.CreateFileSystem().ListAsync(KnownPaths.InternalStorage, cancellation.Token));
    }

    [Fact]
    public async Task Falls_back_to_v1_listing_when_ls_v2_is_absent()
    {
        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Features.Remove("ls_v2");
        fixture.Server.Features.Remove("stat_v2");
        fixture.Server.Files.AddFile("/storage/emulated/0/DCIM/Camera/old.jpg", "x");

        using var cancellation = Cancellation();

        // Older devices must still work, through the 32-bit path.
        var entries = await fixture.CreateFileSystem().ListAsync(KnownPaths.Camera, cancellation.Token);

        var entry = Assert.Single(entries);
        Assert.Equal("old.jpg", entry.Name);
        Assert.Equal(1, entry.Size);
    }

    [Fact]
    public async Task Device_operations_round_trip()
    {
        await using var fixture = FakeServerFixture.Start();
        using var cancellation = Cancellation();
        var fileSystem = fixture.CreateFileSystem();

        var folder = KnownPaths.InternalStorage.Combine("مجلد جديد");
        await fileSystem.CreateDirectoryAsync(folder, cancellation.Token);
        Assert.True(await fileSystem.ExistsAsync(folder, cancellation.Token));

        var renamed = KnownPaths.InternalStorage.Combine("renamed 🌴");
        await fileSystem.RenameAsync(folder, renamed, cancellation.Token);
        Assert.False(await fileSystem.ExistsAsync(folder, cancellation.Token));
        Assert.True(await fileSystem.ExistsAsync(renamed, cancellation.Token));

        await fileSystem.DeleteAsync(renamed, recursive: true, cancellation.Token);
        Assert.False(await fileSystem.ExistsAsync(renamed, cancellation.Token));
    }

    [Fact]
    public async Task Reads_storage_capacity()
    {
        await using var fixture = FakeServerFixture.Start();
        using var cancellation = Cancellation();

        var volumes = await fixture.CreateFileSystem().GetStorageAsync(cancellation.Token);

        var internalStorage = volumes.First();
        Assert.Equal(256L * 1024 * 1024 * 1024, internalStorage.TotalBytes);
        Assert.Equal(82L * 1024 * 1024 * 1024, internalStorage.FreeBytes);
        Assert.False(internalStorage.IsRemovable);
    }

    [Fact]
    public async Task Computes_a_device_side_hash()
    {
        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Files.AddFile("/storage/emulated/0/Download/data.bin", "hash me");

        using var cancellation = Cancellation();
        var path = KnownPaths.Download.Combine("data.bin");

        var hash = await fixture.CreateFileSystem().ComputeSha256Async(path, cancellation.Token);

        Assert.Equal(fixture.Server.Files.Sha256("/storage/emulated/0/Download/data.bin"), hash);
    }

    [Fact]
    public async Task A_device_without_sha256sum_reports_it_as_unsupported()
    {
        await using var fixture = FakeServerFixture.Start();
        fixture.Server.Faults.NoSha256Sum = true;

        using var cancellation = Cancellation();
        var capabilities = FakeServerFixture.FullCapabilities with { HasSha256Sum = false };

        await Assert.ThrowsAsync<CapabilityNotSupportedException>(
            () => fixture.CreateFileSystem(capabilities)
                .ComputeSha256Async(KnownPaths.Camera.Combine("x"), cancellation.Token));
    }

    [Fact]
    public async Task Storage_roots_cannot_be_deleted()
    {
        await using var fixture = FakeServerFixture.Start();
        using var cancellation = Cancellation();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => fixture.CreateFileSystem()
                .DeleteAsync(KnownPaths.InternalStorage, recursive: true, cancellation.Token));
    }
}
