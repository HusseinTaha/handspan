using System.Diagnostics;
using Handspan.Core.Exceptions;
using Handspan.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Handspan.Adb;

/// <summary>Owns the lifetime of the ADB server process (spec §72).</summary>
public interface IAdbServer
{
    /// <summary>Path of the adb binary in use, once located.</summary>
    string? BinaryPath { get; }

    /// <summary>Protocol version reported by the running server.</summary>
    int? ServerVersion { get; }

    /// <summary>True when this application started the server, rather than finding one already running.</summary>
    bool StartedByUs { get; }

    /// <summary>TCP port the server listens on.</summary>
    int Port { get; }

    /// <summary>Ensures a server is reachable, starting one if necessary. Returns its protocol version.</summary>
    Task<int> EnsureRunningAsync(CancellationToken cancellationToken);

    /// <summary>Kills and restarts the server, for the diagnostics page (spec §49).</summary>
    Task RestartAsync(CancellationToken cancellationToken);
}

internal sealed class AdbServerManager(
    IAdbBinaryProvider binaryProvider,
    ILogger<AdbServerManager> logger) : IAdbServer
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? BinaryPath { get; private set; }

    public int? ServerVersion { get; private set; }

    public bool StartedByUs { get; private set; }

    public int Port { get; } = AdbProtocol.DefaultPort;

    public async Task<int> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (ServerVersion is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ServerVersion is { } raced)
            {
                return raced;
            }

            // A server may already be running — started by Android Studio, scrcpy, or a previous
            // session. Reuse it: killing someone else's server is hostile (spec §72).
            var version = await TryQueryVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is { } existing)
            {
                StartedByUs = false;
                ServerVersion = existing;
                logger.LogInformation(
                    "Reusing an ADB server that was already running (protocol version {Version}). "
                    + "Another tool may own it, so it will not be restarted automatically.",
                    existing);
                return existing;
            }

            await StartServerAsync(cancellationToken).ConfigureAwait(false);

            var started = await TryQueryVersionAsync(cancellationToken).ConfigureAwait(false)
                          ?? throw AdbServerException.StartFailed("server did not answer after starting");

            StartedByUs = true;
            ServerVersion = started;
            logger.LogInformation("Started the ADB server (protocol version {Version}).", started);
            return started;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServerVersion = null;

            try
            {
                await using var socket = await AdbSocket.ConnectAsync(Port, cancellationToken)
                    .ConfigureAwait(false);
                await socket.SendServiceAsync(AdbProtocol.HostKill, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // Nothing listening, or it died as we asked — either way it is not running now.
            }

            logger.LogInformation("ADB server stopped on user request.");
        }
        finally
        {
            _gate.Release();
        }

        await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> TryQueryVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var socket = await AdbSocket.ConnectAsync(Port, cancellationToken)
                .ConfigureAwait(false);
            await socket.SendServiceAsync(AdbProtocol.HostVersion, cancellationToken).ConfigureAwait(false);

            var body = await socket.ReadLengthPrefixedStringAsync(cancellationToken).ConfigureAwait(false);

            // The body is the version as hex digits, e.g. "0029" for 41.
            return int.TryParse(body, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var version)
                ? version
                : null;
        }
        catch (DeviceException)
        {
            return null;
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        var binary = BinaryPath
                     ?? await binaryProvider.LocateAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw AdbServerException.NotFound();

        BinaryPath = binary;

        var startInfo = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("start-server");

        using var process = Process.Start(startInfo)
                            ?? throw AdbServerException.StartFailed("could not launch the adb binary");

        // A hung start must not hang the application (spec §72).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw AdbServerException.StartFailed("adb start-server timed out");
        }

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw AdbServerException.StartFailed($"adb start-server exited {process.ExitCode}: {stderr.Trim()}");
        }
    }
}
