using System.Collections.Concurrent;
using Handspan.Core.Interfaces;
using Handspan.Core.Models;

namespace Handspan.Media;

/// <summary>
/// Serves media for preview without leaving a permanent local copy (spec §57, §58).
/// </summary>
/// <remarks>
/// Video and audio are streamed through <see cref="DeviceStreamServer"/>, so a player can seek without the
/// file ever being downloaded. Images are small enough to buffer, and buffering avoids a decoder holding a
/// device socket open while the user looks at the picture.
/// </remarks>
public sealed class MediaPreviewService(DeviceStreamServer server) : IMediaPreviewService
{
    /// <summary>Registered stream URLs, so repeatedly opening the same file does not leak registrations.</summary>
    private readonly ConcurrentDictionary<string, Uri> _urls = new();

    private readonly ConcurrentDictionary<DeviceId, IDeviceFileSystem> _fileSystems = new();

    /// <summary>Associates a device with the filesystem its streams should be read through.</summary>
    public void Register(DeviceId device, IDeviceFileSystem fileSystem)
        => _fileSystems[device] = fileSystem;

    public void Unregister(DeviceId device)
    {
        _fileSystems.TryRemove(device, out _);

        foreach (var key in _urls.Keys.Where(key => key.StartsWith(device.Serial + "|", StringComparison.Ordinal))
                     .ToList())
        {
            if (_urls.TryRemove(key, out var url))
            {
                server.Unregister(url);
            }
        }
    }

    public async Task<Uri> GetStreamUrlAsync(
        DeviceId deviceId,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        if (!_fileSystems.TryGetValue(deviceId, out var fileSystem))
        {
            throw new InvalidOperationException(
                "No connected device is associated with that stream request.");
        }

        var key = $"{deviceId.Serial}|{path.Value}";
        if (_urls.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var info = await fileSystem.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        var url = server.Register(fileSystem, path, info.Size);

        _urls[key] = url;
        return url;
    }

    public async Task<Stream> OpenImageAsync(
        DeviceId deviceId,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        if (!_fileSystems.TryGetValue(deviceId, out var fileSystem))
        {
            throw new InvalidOperationException(
                "No connected device is associated with that preview request.");
        }

        var buffer = new MemoryStream();
        await fileSystem.DownloadAsync(path, buffer, null, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }
}
