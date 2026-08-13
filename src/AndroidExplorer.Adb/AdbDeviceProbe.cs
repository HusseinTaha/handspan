using System.Globalization;
using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Adb;

/// <summary>Gathers the device dashboard's contents (spec §6) and negotiates capabilities (spec §77).</summary>
public interface IAdbDeviceProbe
{
    /// <summary>
    /// Collects device details. Every field beyond identity is best-effort: a phone that will not
    /// report its battery level must never block browsing (spec §6).
    /// </summary>
    Task<DeviceInfo> ProbeAsync(DeviceId device, DeviceState state, CancellationToken cancellationToken);
}

internal sealed class AdbDeviceProbe(
    IAdbHostClient host,
    IAdbShellClient shell,
    IAdbFileLister lister,
    IAdbServer server,
    ILogger<AdbDeviceProbe> logger) : IAdbDeviceProbe
{
    public async Task<DeviceInfo> ProbeAsync(
        DeviceId device,
        DeviceState state,
        CancellationToken cancellationToken)
    {
        var info = new DeviceInfo
        {
            Id = device,
            State = state,
            ConnectionType = device.IsWireless ? ConnectionType.Wireless : ConnectionType.Usb,
            AdbVersion = server.ServerVersion?.ToString(CultureInfo.InvariantCulture),
        };

        // Nothing below this point is reachable on a device that has not authorized us.
        if (state != DeviceState.Online)
        {
            return info;
        }

        var features = await SafeAsync(
            () => host.GetFeaturesAsync(device, cancellationToken),
            fallback: (IReadOnlySet<string>)new HashSet<string>(),
            "feature negotiation").ConfigureAwait(false);

        var properties = await SafeAsync(
            () => ReadPropertiesAsync(device, cancellationToken),
            fallback: new DeviceProperties(),
            "device properties").ConfigureAwait(false);

        var capabilities = await ProbeCapabilitiesAsync(device, features, cancellationToken)
            .ConfigureAwait(false);

        var storage = await SafeAsync(
            () => ReadStorageAsync(device, cancellationToken),
            fallback: (StorageInfo?)null,
            "storage").ConfigureAwait(false);

        var battery = await SafeAsync(
            () => ReadBatteryAsync(device, cancellationToken),
            fallback: (int?)null,
            "battery").ConfigureAwait(false);

        return info with
        {
            Manufacturer = properties.Manufacturer,
            Model = properties.Model,
            AndroidVersion = properties.Release,
            ApiLevel = properties.ApiLevel,
            Storage = storage,
            BatteryPercent = battery,
            Capabilities = capabilities,
        };
    }

    /// <summary>
    /// Reads the four properties the dashboard needs in a single round trip. Absent properties print
    /// an empty line, so the line positions stay stable.
    /// </summary>
    private async Task<DeviceProperties> ReadPropertiesAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        const string command = "getprop ro.product.manufacturer; getprop ro.product.model; "
                               + "getprop ro.build.version.release; getprop ro.build.version.sdk";

        var output = await shell.ExecuteAsync(device, command, mergeStandardError: false, cancellationToken)
            .ConfigureAwait(false);

        var lines = output.Split('\n').Select(line => line.Trim()).ToArray();

        return new DeviceProperties
        {
            Manufacturer = Value(lines, 0),
            Model = Value(lines, 1),
            Release = Value(lines, 2),
            ApiLevel = int.TryParse(Value(lines, 3), out var api) ? api : null,
        };

        static string? Value(string[] lines, int index)
            => index < lines.Length && lines[index].Length > 0 ? lines[index] : null;
    }

    /// <summary>Reads shared-storage capacity via <c>stat -f</c>, which needs no elevated access.</summary>
    private async Task<StorageInfo?> ReadStorageAsync(DeviceId device, CancellationToken cancellationToken)
    {
        var command = "stat -f -c '%b %a %S' " + ShellQuote.Quote(KnownPaths.InternalStorage);

        var output = await shell.ExecuteAsync(device, command, mergeStandardError: false, cancellationToken)
            .ConfigureAwait(false);

        var fields = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3
            || !long.TryParse(fields[0], out var totalBlocks)
            || !long.TryParse(fields[1], out var availableBlocks)
            || !long.TryParse(fields[2], out var blockSize))
        {
            return null;
        }

        return new StorageInfo
        {
            Root = KnownPaths.InternalStorage,
            TotalBytes = totalBlocks * blockSize,
            FreeBytes = availableBlocks * blockSize,
        };
    }

    /// <summary>
    /// Reads battery level, preferring the sysfs file and falling back to <c>dumpsys</c>, which is
    /// much heavier.
    /// </summary>
    private async Task<int?> ReadBatteryAsync(DeviceId device, CancellationToken cancellationToken)
    {
        var sysfs = await shell.ExecuteAsync(
                device,
                "cat /sys/class/power_supply/battery/capacity",
                mergeStandardError: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (int.TryParse(sysfs.Trim(), out var level) && level is >= 0 and <= 100)
        {
            return level;
        }

        var dumpsys = await shell.ExecuteAsync(
                device,
                "dumpsys battery | grep level",
                mergeStandardError: false,
                cancellationToken)
            .ConfigureAwait(false);

        var digits = new string(dumpsys.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var fallback) && fallback is >= 0 and <= 100 ? fallback : null;
    }

    /// <summary>
    /// Determines capabilities by probing behavior, not by inferring from the Android version — OEM
    /// behavior varies more than version numbers suggest (spec §77).
    /// </summary>
    private async Task<DeviceCapabilities> ProbeCapabilitiesAsync(
        DeviceId device,
        IReadOnlySet<string> features,
        CancellationToken cancellationToken)
    {
        var hasStatV2 = features.Contains(AdbProtocol.FeatureStatV2);
        var hasLsV2 = features.Contains(AdbProtocol.FeatureLsV2);

        var canBrowse = false;
        try
        {
            await lister.ListAsync(device, KnownPaths.InternalStorage, cancellationToken)
                .ConfigureAwait(false);
            canBrowse = true;
        }
        catch (DeviceException ex)
        {
            logger.LogWarning("Shared storage is not listable on this device: {Reason}", ex.UserMessage);
        }

        // Probed without writing anything: piping into sha256sum tells us whether it exists.
        var hasSha256 = false;
        try
        {
            var output = await shell.ExecuteAsync(
                    device, "echo -n '' | sha256sum", mergeStandardError: true, cancellationToken)
                .ConfigureAwait(false);

            hasSha256 = output.Trim().Length >= 64 && output.Trim()[..64].All(Uri.IsHexDigit);
        }
        catch (DeviceException)
        {
            hasSha256 = false;
        }

        return new DeviceCapabilities
        {
            CanBrowseSharedStorage = canBrowse,

            // Write capabilities start optimistic and are confirmed the first time one is used, so
            // that session startup never writes to the user's storage merely to ask a question.
            // Phase 3 refines these when the transfer engine lands.
            CanUpload = canBrowse,
            CanDownload = canBrowse,
            CanDelete = canBrowse,
            CanRename = canBrowse,
            CanCreateDirectory = canBrowse,

            CanStream = canBrowse,
            CanWirelessAdb = true,

            HasStatV2 = hasStatV2,
            HasLsV2 = hasLsV2,
            HasShellV2 = features.Contains(AdbProtocol.FeatureShellV2),
            HasSendRecvV2 = features.Contains(AdbProtocol.FeatureSendRecvV2),
            HasSha256Sum = hasSha256,

            // Probed in phase 3, where the resumable-push path actually needs it.
            HasDdNoTrunc = false,
        };
    }

    /// <summary>Runs a best-effort probe, logging and falling back rather than failing the session.</summary>
    private async Task<T> SafeAsync<T>(Func<Task<T>> probe, T fallback, string what)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceException or FormatException or IOException)
        {
            logger.LogDebug(ex, "Optional device probe failed: {What}", what);
            return fallback;
        }
    }

    private sealed record DeviceProperties
    {
        public string? Manufacturer { get; init; }

        public string? Model { get; init; }

        public string? Release { get; init; }

        public int? ApiLevel { get; init; }
    }
}
