using System.Runtime.CompilerServices;
using Handspan.Core.Models;

namespace Handspan.Adb;

/// <summary>One line of the ADB server's device list.</summary>
public readonly record struct AdbDeviceListEntry(DeviceId Id, DeviceState State);

/// <summary>Host-level ADB services: device enumeration, tracking and feature negotiation.</summary>
public interface IAdbHostClient
{
    /// <summary>One-shot device list.</summary>
    Task<IReadOnlyList<AdbDeviceListEntry>> GetDevicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams the device list, pushing a fresh snapshot on every change.
    /// </summary>
    /// <remarks>
    /// This is why hot-plug detection needs no polling and no platform code: the ADB server already
    /// watches USB on both Windows and macOS, and pushes to us (spec §38, §45).
    /// </remarks>
    IAsyncEnumerable<IReadOnlyList<AdbDeviceListEntry>> TrackDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Features the device and server have in common, e.g. <c>stat_v2</c>, <c>ls_v2</c>.</summary>
    Task<IReadOnlySet<string>> GetFeaturesAsync(DeviceId device, CancellationToken cancellationToken);

    Task<DeviceState> GetStateAsync(DeviceId device, CancellationToken cancellationToken);

    /// <summary>
    /// Pairs with a device over Wi-Fi using the six-digit code from Android's wireless debugging screen
    /// (spec §40, Android 11+).
    /// </summary>
    Task PairAsync(string host, int port, string code, CancellationToken cancellationToken);

    /// <summary>Connects to an already-paired wireless device. It then appears on the normal device list.</summary>
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);

    Task DisconnectAsync(string host, int port, CancellationToken cancellationToken);
}

internal sealed class AdbHostClient(IAdbConnectionFactory connections) : IAdbHostClient
{
    public async Task<IReadOnlyList<AdbDeviceListEntry>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync(AdbProtocol.HostDevicesLong, cancellationToken).ConfigureAwait(false);

        var payload = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseDeviceList(payload);
    }

    public async IAsyncEnumerable<IReadOnlyList<AdbDeviceListEntry>> TrackDevicesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync(AdbProtocol.HostTrackDevices, cancellationToken).ConfigureAwait(false);

        // The server sends the current list immediately, then one message per change, forever.
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);
            yield return ParseDeviceList(payload);
        }
    }

    public async Task<IReadOnlySet<string>> GetFeaturesAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync(AdbProtocol.HostFeatures(device), cancellationToken)
            .ConfigureAwait(false);

        var payload = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);

        return payload
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<DeviceState> GetStateAsync(DeviceId device, CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync(AdbProtocol.HostGetState(device), cancellationToken)
            .ConfigureAwait(false);

        var payload = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseState(payload.Trim());
    }

    public async Task PairAsync(
        string host,
        int port,
        string code,
        CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync(AdbProtocol.HostPair(code, host, port), cancellationToken)
            .ConfigureAwait(false);

        var response = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);

        // The server answers OKAY even when pairing fails, and explains itself in the payload.
        if (!response.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdbProtocolException(response.Trim().Length > 0
                ? response.Trim()
                : "pairing was refused");
        }
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync(AdbProtocol.HostConnect(host, port), cancellationToken)
            .ConfigureAwait(false);

        var response = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || response.Contains("unable", StringComparison.OrdinalIgnoreCase)
            || response.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdbProtocolException(response.Trim());
        }
    }

    public async Task DisconnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await using var socket = await connections.OpenHostAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendServiceAsync($"host:disconnect:{host}:{port}", cancellationToken)
            .ConfigureAwait(false);
        await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses both the short tracking format ("serial\tstate\n") and the long one, which appends
    /// <c>product:</c>, <c>model:</c>, <c>device:</c> and <c>transport_id:</c> fields.
    /// </summary>
    internal static IReadOnlyList<AdbDeviceListEntry> ParseDeviceList(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        var entries = new List<AdbDeviceListEntry>();

        foreach (var rawLine in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            entries.Add(new AdbDeviceListEntry(new DeviceId(fields[0]), ParseState(fields[1])));
        }

        return entries;
    }

    /// <summary>Maps the server's state word onto <see cref="DeviceState"/> (spec §5).</summary>
    internal static DeviceState ParseState(string state) => state switch
    {
        "device" => DeviceState.Online,

        // "authorizing" is the brief window after the user taps Allow; treating it as unauthorized
        // keeps the UI on the walkthrough until the device is genuinely usable.
        "unauthorized" or "authorizing" => DeviceState.Unauthorized,

        "offline" => DeviceState.Offline,

        // bootloader, recovery, sideload, rescue, host, "no permissions" — connected, but not a
        // device we can browse.
        _ => DeviceState.Unknown,
    };
}
