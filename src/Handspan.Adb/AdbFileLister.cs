using Handspan.Core.Exceptions;
using Handspan.Core.Models;
using Microsoft.Extensions.Logging;

namespace Handspan.Adb;

/// <summary>
/// Structured directory listing and stat over the sync protocol.
/// </summary>
/// <remarks>
/// Phase 2's <c>IDeviceFileSystem</c> is built on this. It is separate so that listing can be used
/// (and validated against real hardware) before the full filesystem abstraction exists.
/// </remarks>
public interface IAdbFileLister
{
    Task<IReadOnlyList<DeviceEntry>> ListAsync(
        DeviceId device,
        DevicePath path,
        CancellationToken cancellationToken);

    Task<DeviceFileInfo> StatAsync(DeviceId device, DevicePath path, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(DeviceId device, DevicePath path, CancellationToken cancellationToken);
}

internal sealed class AdbFileLister(
    IAdbConnectionFactory connections,
    IAdbHostClient host,
    ILogger<AdbFileLister> logger) : IAdbFileLister
{
    private readonly Dictionary<DeviceId, IReadOnlySet<string>> _featureCache = [];
    private readonly SemaphoreSlim _featureGate = new(1, 1);

    public async Task<IReadOnlyList<DeviceEntry>> ListAsync(
        DeviceId device,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        var features = await GetFeaturesAsync(device, cancellationToken).ConfigureAwait(false);
        var useV2 = features.Contains(AdbProtocol.FeatureLsV2);

        await using var sync = await AdbSyncClient.OpenAsync(connections, device, cancellationToken)
            .ConfigureAwait(false);

        var entries = await sync.ListAsync(device, path, useV2, cancellationToken).ConfigureAwait(false);

        // /sdcard is a symlink to /storage/emulated/0 on essentially every modern device, and OEMs
        // add more. Resolving them here is what makes such entries navigable rather than dead ends.
        return await ResolveSymlinksAsync(device, entries, features, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeviceFileInfo> StatAsync(
        DeviceId device,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        var features = await GetFeaturesAsync(device, cancellationToken).ConfigureAwait(false);

        await using var sync = await AdbSyncClient.OpenAsync(connections, device, cancellationToken)
            .ConfigureAwait(false);

        return await sync
            .StatAsync(device, path, features.Contains(AdbProtocol.FeatureStatV2), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(
        DeviceId device,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        var features = await GetFeaturesAsync(device, cancellationToken).ConfigureAwait(false);

        await using var sync = await AdbSyncClient.OpenAsync(connections, device, cancellationToken)
            .ConfigureAwait(false);

        return await sync
            .ExistsAsync(device, path, features.Contains(AdbProtocol.FeatureStatV2), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DeviceEntry>> ResolveSymlinksAsync(
        DeviceId device,
        IReadOnlyList<DeviceEntry> entries,
        IReadOnlySet<string> features,
        CancellationToken cancellationToken)
    {
        if (entries.All(entry => entry.Kind != DeviceEntryKind.Symlink))
        {
            return entries;
        }

        var useStatV2 = features.Contains(AdbProtocol.FeatureStatV2);
        var resolved = new List<DeviceEntry>(entries.Count);

        await using var sync = await AdbSyncClient.OpenAsync(connections, device, cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in entries)
        {
            if (entry.Kind != DeviceEntryKind.Symlink)
            {
                resolved.Add(entry);
                continue;
            }

            try
            {
                // STAT follows the link, so the result describes the target.
                var target = await sync.StatAsync(device, entry.Path, useStatV2, cancellationToken)
                    .ConfigureAwait(false);

                resolved.Add(entry with
                {
                    Kind = target.Kind,
                    Size = target.Size,
                    IsSizeKnown = target.IsSizeKnown,
                });
            }
            catch (DeviceException)
            {
                // A broken or inaccessible link stays listed as a symlink rather than disappearing.
                logger.LogDebug("Could not resolve a symlink in a listing; leaving it unresolved.");
                resolved.Add(entry);
            }
        }

        return resolved;
    }

    private async Task<IReadOnlySet<string>> GetFeaturesAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        await _featureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_featureCache.TryGetValue(device, out var cached))
            {
                return cached;
            }

            var features = await host.GetFeaturesAsync(device, cancellationToken).ConfigureAwait(false);
            _featureCache[device] = features;
            return features;
        }
        finally
        {
            _featureGate.Release();
        }
    }
}
