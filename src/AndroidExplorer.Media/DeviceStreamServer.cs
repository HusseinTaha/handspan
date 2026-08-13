using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Media;

/// <summary>
/// Serves device files over loopback HTTP with range support (spec §58).
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a media player seek inside a video that lives on the phone without downloading it
/// first: the player issues range requests, and each one becomes a bounded read on the device (spec §24).
/// The same server later feeds ffmpeg for video frame extraction.
/// </para>
/// <para>
/// Security matters more here than the small surface suggests, because this opens a socket that can read the
/// user's phone. Three defences: it binds to 127.0.0.1 only, every URL carries a per-session random token,
/// and only paths explicitly registered for streaming can be served — a caller cannot walk the device by
/// guessing URLs.
/// </para>
/// </remarks>
public sealed class DeviceStreamServer : IAsyncDisposable
{
    private readonly ILogger<DeviceStreamServer> _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Registered streams, keyed by an unguessable id. Nothing else can be served.</summary>
    private readonly ConcurrentDictionary<string, StreamEntry> _entries = new();

    private Task? _loop;

    public DeviceStreamServer(ILogger<DeviceStreamServer> logger)
    {
        _logger = logger;

        // A random token in every path, so another local process cannot read the phone by guessing URLs.
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public string Token { get; }

    public int Port { get; private set; }

    public bool IsRunning => _loop is not null;

    /// <summary>Starts on a free loopback port.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        Port = FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        _loop = Task.Run(AcceptLoopAsync, CancellationToken.None);
        _logger.LogInformation("Media streaming server listening on loopback port {Port}.", Port);
    }

    /// <summary>
    /// Registers a file for streaming and returns its loopback URL.
    /// </summary>
    /// <remarks>
    /// Registration is what authorizes a path: without it the server refuses, so a compromised or curious
    /// local process cannot use this as a general-purpose read of the device.
    /// </remarks>
    public Uri Register(IDeviceFileSystem fileSystem, DevicePath path, long size, string? mimeType = null)
    {
        if (!IsRunning)
        {
            Start();
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _entries[id] = new StreamEntry(fileSystem, path, size, mimeType ?? GuessMimeType(path));

        return new Uri($"http://127.0.0.1:{Port}/{Token}/{id}");
    }

    public void Unregister(Uri url)
    {
        var id = url.Segments.LastOrDefault()?.Trim('/');
        if (id is not null)
        {
            _entries.TryRemove(id, out _);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                return;
            }

            // One slow player must not block the next request.
            _ = Task.Run(() => ServeAsync(context), CancellationToken.None);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            if (!TryResolve(context.Request.Url, out var entry))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.Headers["Accept-Ranges"] = "bytes";
            response.ContentType = entry!.MimeType;

            var (offset, length, isPartial) = ParseRange(
                context.Request.Headers["Range"], entry.Size);

            if (offset >= entry.Size && entry.Size > 0)
            {
                // A player seeking past the end gets the documented answer, not a hang.
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.Headers["Content-Range"] = $"bytes */{entry.Size}";
                return;
            }

            if (isPartial)
            {
                response.StatusCode = (int)HttpStatusCode.PartialContent;
                response.Headers["Content-Range"] =
                    $"bytes {offset}-{offset + length - 1}/{entry.Size}";
            }

            response.ContentLength64 = length;

            // HEAD is how some players probe for range support before committing to a stream.
            if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await CopyRangeAsync(entry, offset, length, response.OutputStream).ConfigureAwait(false);
        }
        catch (DeviceException ex)
        {
            _logger.LogDebug("Streaming request failed: {Reason}", ex.UserMessage);
            TrySetStatus(response, HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException
                                       or ObjectDisposedException)
        {
            // The player closed the connection — routine when seeking.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Validates the token and looks up a registered stream. Both must match.</summary>
    private bool TryResolve(Uri? url, out StreamEntry? entry)
    {
        entry = null;

        var segments = url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not { Length: 2 })
        {
            return false;
        }

        // Fixed-time comparison: the token is a secret, and this endpoint is reachable by any local process.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(segments[0]), Encoding.ASCII.GetBytes(Token)))
        {
            return false;
        }

        return _entries.TryGetValue(segments[1], out entry);
    }

    /// <summary>Parses a single-range request; multi-range is answered as a whole-file response.</summary>
    internal static (long Offset, long Length, bool IsPartial) ParseRange(string? header, long size)
    {
        if (string.IsNullOrWhiteSpace(header)
            || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (0, size, false);
        }

        var spec = header[6..].Split(',')[0].Trim();
        var separator = spec.IndexOf('-');
        if (separator < 0)
        {
            return (0, size, false);
        }

        var fromText = spec[..separator];
        var toText = spec[(separator + 1)..];

        // "bytes=-500" means the last 500 bytes, which players use to read a trailing moov atom.
        if (fromText.Length == 0)
        {
            if (!long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tail)
                || tail <= 0)
            {
                return (0, size, false);
            }

            var start = Math.Max(0, size - tail);
            return (start, size - start, true);
        }

        if (!long.TryParse(fromText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            || offset < 0)
        {
            return (0, size, false);
        }

        var end = size - 1;
        if (toText.Length > 0
            && long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEnd))
        {
            end = Math.Min(parsedEnd, size - 1);
        }

        var length = Math.Max(0, end - offset + 1);
        return (offset, length, true);
    }

    private static async Task CopyRangeAsync(
        StreamEntry entry,
        long offset,
        long length,
        Stream destination)
    {
        const int chunkSize = 256 * 1024;
        var remaining = length;
        var position = offset;

        while (remaining > 0)
        {
            var want = (int)Math.Min(chunkSize, remaining);

            var chunk = await entry.FileSystem
                .ReadRangeAsync(entry.Path, position, want, CancellationToken.None)
                .ConfigureAwait(false);

            if (chunk.Length == 0)
            {
                return;
            }

            await destination.WriteAsync(chunk).ConfigureAwait(false);

            position += chunk.Length;
            remaining -= chunk.Length;
        }
    }

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void TrySetStatus(HttpListenerResponse response, HttpStatusCode status)
    {
        try
        {
            response.StatusCode = (int)status;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static string GuessMimeType(DevicePath path) => path.Extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".3gp" => "video/3gpp",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" or ".opus" => "audio/ogg",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".heic" or ".heif" => "image/heic",
        _ => "application/octet-stream",
    };

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown; nothing actionable.
            }
        }

        _entries.Clear();
        _shutdown.Dispose();
    }

    private sealed record StreamEntry(
        IDeviceFileSystem FileSystem,
        DevicePath Path,
        long Size,
        string MimeType);
}
