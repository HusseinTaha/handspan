using System.Collections.Concurrent;
using AndroidExplorer.Adb;
using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Media;
using AndroidExplorer.Search;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Services;

/// <summary>
/// Tracks connected devices and owns their sessions (spec §69).
/// </summary>
/// <remarks>
/// Detection is push-based: the ADB server watches USB and streams the device list to us, so there is
/// no polling and no platform-specific device-notification code on either OS (spec §38, §45).
/// </remarks>
internal sealed class DeviceManager(
    IAdbServer server,
    IAdbHostClient host,
    IAdbDeviceProbe probe,
    IAdbFileSystemFactory fileSystemFactory,
    ICacheService cache,
    ITransferManagerFactory transfers,
    IThumbnailServiceFactory thumbnails,
    IGalleryServiceFactory gallery,
    ISearchServiceFactory search,
    MediaPreviewService previews,
    ILogger<DeviceManager> logger) : IDeviceManager
{
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<DeviceId, DeviceInfo> _devices = new();
    private readonly ConcurrentDictionary<DeviceId, DeviceSession> _sessions = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private CancellationTokenSource? _tracking;
    private Task? _trackingLoop;

    public IReadOnlyList<DeviceInfo> Devices => _devices.Values.OrderBy(device => device.Id).ToList();

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_trackingLoop is not null)
        {
            return;
        }

        await server.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        // The loop outlives the caller's token deliberately: it runs for the application's lifetime.
        _tracking = new CancellationTokenSource();
        _trackingLoop = Task.Run(() => TrackAsync(_tracking.Token), CancellationToken.None);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await host.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        await ApplySnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAdbAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Restarting the ADB server on user request.");

        foreach (var id in _sessions.Keys)
        {
            await DisconnectAsync(id, cancellationToken).ConfigureAwait(false);
        }

        await server.RestartAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PairWirelessAsync(
        string address,
        int port,
        string code,
        CancellationToken cancellationToken)
    {
        await host.PairAsync(address, port, code, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Paired with a device over Wi-Fi.");
    }

    public async Task ConnectWirelessAsync(string address, int port, CancellationToken cancellationToken)
    {
        await host.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);

        // The device now appears on the tracking stream like any other; refresh so it shows immediately.
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Connected to a device over Wi-Fi.");
    }

    /// <summary>Pauses every device's queue, e.g. before sleep (spec §81).</summary>
    public async Task PauseAllTransfersAsync(string reason)
    {
        foreach (var session in _sessions.Values)
        {
            await session.Transfers.PauseAllAsync(reason).ConfigureAwait(false);
        }
    }

    public async Task ResumeAllTransfersAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.Transfers.ResumeAllAsync().ConfigureAwait(false);
        }
    }

    public async Task<IDeviceSession> ConnectAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(deviceId, out var existing))
        {
            return existing;
        }

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(deviceId, out var raced))
            {
                return raced;
            }

            if (!_devices.TryGetValue(deviceId, out var info))
            {
                throw new DeviceDisconnectedException(technicalDetail: "device is not in the current list");
            }

            if (info.State == DeviceState.Unauthorized)
            {
                throw new DeviceUnauthorizedException();
            }

            if (info.State != DeviceState.Online)
            {
                throw new DeviceOfflineException($"state is {info.State}");
            }

            var session = new DeviceSession(
                info, fileSystemFactory, cache, transfers, thumbnails, gallery, search);

            // Journalled transfers come back as paused, ready to resume (spec §13).
            await session.RestoreTransfersAsync(cancellationToken).ConfigureAwait(false);

            // Lets the streaming server read this device's files for playback and open-with (spec §58).
            previews.Register(deviceId, session.FileSystem);

            _sessions[deviceId] = session;
            logger.LogInformation("Opened a session for a device.");
            return session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task DisconnectAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        if (_sessions.TryRemove(deviceId, out var session))
        {
            // Drop the streaming registrations first: a URL that outlived its device would serve errors.
            previews.Unregister(deviceId);

            await session.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("Closed a device session.");
        }
    }

    /// <summary>
    /// Consumes the tracking stream, reconnecting with exponential backoff when the server dies
    /// (spec §72).
    /// </summary>
    private async Task TrackAsync(CancellationToken cancellationToken)
    {
        var backoff = MinimumBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in host.TrackDevicesAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    backoff = MinimumBackoff;
                    await ApplySnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceException ex)
            {
                logger.LogWarning(
                    "Device tracking stopped ({Reason}); retrying in {Delay}.", ex.UserMessage, backoff);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2,
                MaximumBackoff.TotalMilliseconds));

            try
            {
                await server.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // Still down. The next iteration retries after a longer delay.
            }
        }
    }

    private async Task ApplySnapshotAsync(
        IReadOnlyList<AdbDeviceListEntry> snapshot,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<DeviceId>();

        foreach (var entry in snapshot)
        {
            seen.Add(entry.Id);

            if (_devices.TryGetValue(entry.Id, out var known))
            {
                if (known.State == entry.State)
                {
                    continue;
                }

                var updated = await DescribeAsync(entry, cancellationToken).ConfigureAwait(false);
                _devices[entry.Id] = updated;
                Raise(updated, DeviceChangeKind.StateChanged);

                if (updated.State != DeviceState.Online)
                {
                    // Pause before tearing the session down, so partial files and resume points are
                    // journalled rather than lost (spec §38).
                    await PauseSessionAsync(entry.Id, $"{updated.DisplayName} stopped responding.")
                        .ConfigureAwait(false);
                    await DisconnectAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var added = await DescribeAsync(entry, cancellationToken).ConfigureAwait(false);
            _devices[entry.Id] = added;
            Raise(added, DeviceChangeKind.Added);
        }

        foreach (var id in _devices.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            if (!_devices.TryRemove(id, out var removed))
            {
                continue;
            }

            // Spec §38: "Transfers paused. Reconnect device to resume." Pausing first keeps the partial
            // files, which is the difference between resuming and starting over.
            await PauseSessionAsync(id, $"{removed.DisplayName} was disconnected.").ConfigureAwait(false);

            await DisconnectAsync(id, cancellationToken).ConfigureAwait(false);
            Raise(removed with { State = DeviceState.Disconnected }, DeviceChangeKind.Removed);
        }
    }

    /// <summary>
    /// Probes device details, bounded by a timeout so one unresponsive phone cannot stall detection
    /// of the others (spec §6).
    /// </summary>
    private async Task<DeviceInfo> DescribeAsync(
        AdbDeviceListEntry entry,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            return await probe.ProbeAsync(entry.Id, entry.State, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceException or OperationCanceledException)
        {
            logger.LogDebug("Device probe was incomplete; showing identity only.");
            return new DeviceInfo
            {
                Id = entry.Id,
                State = entry.State,
                ConnectionType = entry.Id.IsWireless ? ConnectionType.Wireless : ConnectionType.Usb,
            };
        }
    }

    private async Task PauseSessionAsync(DeviceId deviceId, string reason)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            await session.Transfers.PauseAllAsync(reason).ConfigureAwait(false);
        }
    }

    private void Raise(DeviceInfo device, DeviceChangeKind kind)
    {
        // Raised on the tracking thread; UI consumers marshal to their own thread (spec §46).
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(device, kind));
    }

    public async ValueTask DisposeAsync()
    {
        if (_tracking is not null)
        {
            await _tracking.CancelAsync().ConfigureAwait(false);
        }

        if (_trackingLoop is not null)
        {
            try
            {
                await _trackingLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _tracking?.Dispose();
        _sessionGate.Dispose();
    }
}
