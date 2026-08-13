using Handspan.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Handspan.Adb.Tests;

/// <summary>
/// Validates our hand-written host protocol against the real ADB server.
/// </summary>
/// <remarks>
/// These tests matter more than they look: the packet layouts in <see cref="AdbSyncClient"/> were
/// written from the protocol's documented structures, and a wrong field offset produces
/// plausible-looking garbage rather than an error. Checking against the reference implementation —
/// and against the adb CLI's own answers — is what catches that.
/// </remarks>
public class RealAdbServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [RequiresAdbFact]
    public async Task Server_reports_a_protocol_version()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var server = provider.GetRequiredService<IAdbServer>();

        using var cancellation = new CancellationTokenSource(Timeout);
        var version = await server.EnsureRunningAsync(cancellation.Token);

        // 41 is the long-standing host protocol version; anything sane is >= 40.
        Assert.True(version >= 40, $"unexpected protocol version {version}");
        Assert.NotNull(server.ServerVersion);
    }

    [RequiresDeviceFact]
    public async Task Device_list_matches_the_adb_cli()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var host = provider.GetRequiredService<IAdbHostClient>();

        using var cancellation = new CancellationTokenSource(Timeout);
        var ours = await host.GetDevicesAsync(cancellation.Token);

        var expected = AdbTestEnvironment.CliDevices
            .Select(device => (device.Serial, State: AdbHostClient.ParseState(device.State)))
            .OrderBy(device => device.Serial)
            .ToList();

        var actual = ours
            .Select(device => (Serial: device.Id.Serial, device.State))
            .OrderBy(device => device.Serial)
            .ToList();

        Assert.Equal(expected, actual);
    }

    [RequiresDeviceFact]
    public async Task Track_devices_pushes_an_immediate_snapshot()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var host = provider.GetRequiredService<IAdbHostClient>();

        using var cancellation = new CancellationTokenSource(Timeout);

        // The server sends the current list as soon as the stream opens — this is what makes hot-plug
        // detection push-based rather than polled (spec §38).
        await foreach (var snapshot in host.TrackDevicesAsync(cancellation.Token))
        {
            Assert.NotEmpty(snapshot);
            Assert.All(snapshot, device => Assert.False(string.IsNullOrWhiteSpace(device.Id.Serial)));
            return;
        }

        Assert.Fail("the tracking stream closed without sending a snapshot");
    }

    [RequiresDeviceFact]
    public async Task Device_state_is_reported_for_a_specific_serial()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var host = provider.GetRequiredService<IAdbHostClient>();

        var serial = AdbTestEnvironment.CliDevices[0].Serial;
        using var cancellation = new CancellationTokenSource(Timeout);

        // An unauthorized device answers get-state with a failure, which must surface as the typed
        // exception the UI knows how to explain (spec §48) rather than a protocol error.
        try
        {
            var state = await host.GetStateAsync(new DeviceId(serial), cancellation.Token);
            Assert.NotEqual(DeviceState.Disconnected, state);
        }
        catch (Core.Exceptions.DeviceUnauthorizedException)
        {
            Assert.False(AdbTestEnvironment.HasOnlineDevice, "an online device should not report unauthorized");
        }
    }

    [RequiresOnlineDeviceFact]
    public async Task Features_are_negotiated()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var host = provider.GetRequiredService<IAdbHostClient>();

        var serial = AdbTestEnvironment.CliDevices.First(d => d.State == "device").Serial;
        using var cancellation = new CancellationTokenSource(Timeout);

        var features = await host.GetFeaturesAsync(new DeviceId(serial), cancellation.Token);

        Assert.NotEmpty(features);

        // Every device new enough to matter has these; if one does not, the fallback paths engage and
        // this assertion is the signal to go verify them.
        Assert.Contains(AdbProtocol.FeatureStatV2, features);
        Assert.Contains(AdbProtocol.FeatureLsV2, features);
    }

    [RequiresOnlineDeviceFact]
    public async Task Shared_storage_lists_with_structured_records()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();

        var serial = AdbTestEnvironment.CliDevices.First(d => d.State == "device").Serial;
        using var cancellation = new CancellationTokenSource(Timeout);

        var entries = await lister.ListAsync(
            new DeviceId(serial), KnownPaths.InternalStorage, cancellation.Token);

        Assert.NotEmpty(entries);

        // "." and ".." are protocol noise and must never reach the UI.
        Assert.DoesNotContain(entries, entry => entry.Name is "." or "..");

        // Every real phone has DCIM; if this fails the listing is being mis-parsed.
        Assert.Contains(entries, entry => entry.Name == "DCIM" && entry.IsDirectory);

        Assert.All(entries, entry =>
        {
            Assert.Equal(KnownPaths.InternalStorage, entry.Path.Parent);
            Assert.NotEqual(default, entry.Modified);
        });
    }

    [RequiresOnlineDeviceFact]
    public async Task Stat_agrees_with_listing_for_the_same_file()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();

        var device = new DeviceId(AdbTestEnvironment.CliDevices.First(d => d.State == "device").Serial);
        using var cancellation = new CancellationTokenSource(Timeout);

        var dcim = await lister.ListAsync(device, KnownPaths.Dcim, cancellation.Token);
        var directory = dcim.FirstOrDefault(entry => entry.IsDirectory);
        Assert.NotNull(directory);

        var stat = await lister.StatAsync(device, directory.Path, cancellation.Token);

        // Two independent protocol paths describing the same object: if the stat_v2 field offsets were
        // wrong, these would disagree.
        Assert.Equal(directory.Path, stat.Path);
        Assert.True(stat.IsDirectory);
        Assert.Equal(directory.Modified.ToUnixTimeSeconds(), stat.Modified.ToUnixTimeSeconds());
    }

    [RequiresOnlineDeviceFact]
    public async Task Sdcard_symlink_resolves_to_a_directory()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();

        var device = new DeviceId(AdbTestEnvironment.CliDevices.First(d => d.State == "device").Serial);
        using var cancellation = new CancellationTokenSource(Timeout);

        // /sdcard is a symlink to /storage/emulated/0 on modern Android; unresolved, it would be a
        // dead end in the UI.
        var info = await lister.StatAsync(device, KnownPaths.InternalStorage, cancellation.Token);

        Assert.True(info.IsDirectory);
    }

    [RequiresOnlineDeviceFact]
    public async Task Missing_paths_raise_a_typed_not_found_error()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var lister = provider.GetRequiredService<IAdbFileLister>();

        var device = new DeviceId(AdbTestEnvironment.CliDevices.First(d => d.State == "device").Serial);
        using var cancellation = new CancellationTokenSource(Timeout);

        var missing = KnownPaths.InternalStorage.Combine("handspan-does-not-exist-9f3c1");

        Assert.False(await lister.ExistsAsync(device, missing, cancellation.Token));
        await Assert.ThrowsAsync<Core.Exceptions.PathNotFoundException>(
            () => lister.StatAsync(device, missing, cancellation.Token));
    }

    [RequiresOnlineDeviceFact]
    public async Task Device_probe_fills_the_dashboard()
    {
        await using var provider = AdbTestEnvironment.BuildProvider();
        var probe = provider.GetRequiredService<IAdbDeviceProbe>();

        var device = new DeviceId(AdbTestEnvironment.CliDevices.First(d => d.State == "device").Serial);
        using var cancellation = new CancellationTokenSource(Timeout);

        var info = await probe.ProbeAsync(device, DeviceState.Online, cancellation.Token);

        Assert.Equal(DeviceState.Online, info.State);
        Assert.False(string.IsNullOrWhiteSpace(info.Manufacturer), "manufacturer should come from getprop");
        Assert.False(string.IsNullOrWhiteSpace(info.Model), "model should come from getprop");
        Assert.NotNull(info.ApiLevel);
        Assert.True(info.Capabilities.CanBrowseSharedStorage);

        // stat -f should report a plausible capacity for shared storage.
        Assert.NotNull(info.Storage);
        Assert.True(info.Storage!.TotalBytes > 1_000_000_000, "expected at least 1 GB of shared storage");
        Assert.InRange(info.Storage.FreeBytes, 0, info.Storage.TotalBytes);
    }
}
