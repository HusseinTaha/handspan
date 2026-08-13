using AndroidExplorer.Core.Exceptions;
using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Adb;

/// <summary>
/// Constants of the ADB host protocol (spec §71).
/// </summary>
/// <remarks>
/// We speak this protocol — localhost TCP to the ADB server — rather than shelling out to the adb
/// CLI, because resume, range reads and structured listings are impossible through the CLI. We do
/// not touch the USB wire protocol; that stays Google's job, which is what §98's warning is about.
/// </remarks>
internal static class AdbProtocol
{
    public const string LocalHost = "127.0.0.1";

    public const int DefaultPort = 5037;

    /// <summary>Maximum payload of a single sync DATA packet, fixed by the protocol.</summary>
    public const int SyncDataMax = 64 * 1024;

    /// <summary>Resume offsets are aligned to this, so only baseline <c>dd</c> semantics are needed.</summary>
    public const long ResumeAlignment = 1024 * 1024;

    // --- host services ---
    public const string HostVersion = "host:version";
    public const string HostDevicesLong = "host:devices-l";
    public const string HostTrackDevices = "host:track-devices";
    public const string HostKill = "host:kill";

    public static string HostFeatures(DeviceId device) => $"host-serial:{device.Serial}:features";

    public static string HostGetState(DeviceId device) => $"host-serial:{device.Serial}:get-state";

    public static string HostTransport(DeviceId device) => $"host:transport:{device.Serial}";

    public static string HostConnect(string host, int port) => $"host:connect:{host}:{port}";

    public static string HostPair(string code, string host, int port) => $"host:pair:{code}:{host}:{port}";

    // --- local services (after a transport switch) ---
    public const string SyncService = "sync:";

    /// <summary>
    /// Raw command execution with stdin and stdout and no PTY translation. Plain <c>shell:</c>
    /// mangles line endings on some devices, which silently corrupts binary streams.
    /// </summary>
    public static string Exec(string command) => $"exec:{command}";

    public static string ShellV2(string command) => $"shell,v2,raw:{command}";

    public static string ShellRaw(string command) => $"shell,raw:{command}";

    // --- negotiated features (spec §1.2) ---
    public const string FeatureStatV2 = "stat_v2";
    public const string FeatureLsV2 = "ls_v2";
    public const string FeatureShellV2 = "shell_v2";
    public const string FeatureSendRecvV2 = "sendrecv_v2";
}

/// <summary>Four-character sync protocol message identifiers.</summary>
internal static class SyncId
{
    public const string List = "LIST";
    public const string ListV2 = "LIS2";
    public const string Dent = "DENT";
    public const string DentV2 = "DNT2";
    public const string Stat = "STAT";
    public const string StatV2 = "STA2";
    public const string LinkStatV2 = "LST2";
    public const string Send = "SEND";
    public const string Recv = "RECV";
    public const string Data = "DATA";
    public const string Done = "DONE";
    public const string Okay = "OKAY";
    public const string Fail = "FAIL";
    public const string Quit = "QUIT";

    /// <summary>Bytes of a v1 DENT or DONE body, after the identifier: mode, size, mtime, namelen.</summary>
    public const int DentV1BodyLength = 16;

    /// <summary>
    /// Bytes of a stat_v2 body after the identifier: error, dev, ino, mode, nlink, uid, gid, size,
    /// atime, mtime, ctime.
    /// </summary>
    public const int StatV2BodyLength = 68;

    /// <summary>Bytes of a DNT2 or v2 DONE body: a stat_v2 body plus a name length.</summary>
    public const int DentV2BodyLength = StatV2BodyLength + 4;
}

/// <summary>POSIX file mode bits needed to classify listing entries.</summary>
internal static class PosixMode
{
    public const int TypeMask = 0xF000;
    public const int Directory = 0x4000;
    public const int Regular = 0x8000;
    public const int Symlink = 0xA000;

    public const int PermissionMask = 0x1FF;

    public static DeviceEntryKind ToKind(int mode) => (mode & TypeMask) switch
    {
        Directory => DeviceEntryKind.Directory,
        Regular => DeviceEntryKind.File,
        Symlink => DeviceEntryKind.Symlink,
        _ => DeviceEntryKind.Other,
    };
}

/// <summary>The ADB server or device sent something we could not interpret.</summary>
public sealed class AdbProtocolException : DeviceException
{
    public AdbProtocolException(string technicalDetail, Exception? inner = null)
        : base("The Android connection returned an unexpected response.", technicalDetail, inner)
    {
    }
}

/// <summary>
/// Turns protocol failure text into the typed exceptions the UI knows how to explain (spec §48).
/// </summary>
internal static class AdbFailure
{
    public static DeviceException Translate(string message, DevicePath? path = null)
    {
        var text = message.ToLowerInvariant();

        if (text.Contains("device unauthorized") || text.Contains("device still authorizing"))
        {
            return new DeviceUnauthorizedException(message);
        }

        if (text.Contains("device offline"))
        {
            return new DeviceOfflineException(message);
        }

        if (text.Contains("device not found") || text.Contains("no devices") || text.Contains("closed"))
        {
            return new DeviceDisconnectedException(technicalDetail: message);
        }

        if (text.Contains("permission denied") || text.Contains("operation not permitted"))
        {
            return new AccessDeniedException(path ?? DevicePath.Root, message);
        }

        if (text.Contains("no such file or directory") || text.Contains("does not exist"))
        {
            return new PathNotFoundException(path ?? DevicePath.Root, message);
        }

        if (text.Contains("no space left"))
        {
            return new InsufficientSpaceException(0, 0, onDevice: true);
        }

        if (text.Contains("transport") && text.Contains("not") )
        {
            return new DeviceDisconnectedException(technicalDetail: message);
        }

        return new AdbProtocolException(message);
    }
}
