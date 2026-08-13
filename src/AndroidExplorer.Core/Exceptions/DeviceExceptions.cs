using AndroidExplorer.Core.Models;

namespace AndroidExplorer.Core.Exceptions;

/// <summary>
/// Base class for failures that have a sensible explanation for the user (spec §48).
/// </summary>
/// <remarks>
/// The UI shows <see cref="UserMessage"/> and never a raw protocol string, stderr dump or exit
/// code. "The Android device disconnected during the transfer" is useful; "exit code 1" is not.
/// </remarks>
public abstract class DeviceException : Exception
{
    protected DeviceException(string userMessage, string? technicalDetail = null, Exception? inner = null)
        : base(technicalDetail is null ? userMessage : $"{userMessage} ({technicalDetail})", inner)
    {
        UserMessage = userMessage;
        TechnicalDetail = technicalDetail;
    }

    /// <summary>Message safe and appropriate to show to the user.</summary>
    public string UserMessage { get; }

    /// <summary>
    /// Diagnostic detail for the log. May contain protocol text, so it is only written when
    /// verbose diagnostics are enabled (spec §43).
    /// </summary>
    public string? TechnicalDetail { get; }

    /// <summary>True when retrying the operation could plausibly succeed.</summary>
    public virtual bool IsTransient => false;

    public DeviceId? DeviceId { get; init; }
}

/// <summary>The device went away mid-operation — cable pull, reboot, or sleep (spec §38).</summary>
public sealed class DeviceDisconnectedException : DeviceException
{
    public DeviceDisconnectedException(string? deviceName = null, string? technicalDetail = null, Exception? inner = null)
        : base(deviceName is null
                ? "The Android device disconnected."
                : $"{deviceName} disconnected.",
            technicalDetail,
            inner)
    {
    }

    public override bool IsTransient => true;
}

/// <summary>Android refused access to a path (spec §48, §78).</summary>
public sealed class AccessDeniedException : DeviceException
{
    public AccessDeniedException(DevicePath path, string? technicalDetail = null, Exception? inner = null)
        : base("Android denied access to this location. It may be protected by Android.",
            technicalDetail,
            inner)
        => Path = path;

    public DevicePath Path { get; }
}

/// <summary>A path that was expected to exist does not.</summary>
public sealed class PathNotFoundException : DeviceException
{
    public PathNotFoundException(DevicePath path, string? technicalDetail = null, Exception? inner = null)
        : base("That file or folder no longer exists on the device.", technicalDetail, inner)
        => Path = path;

    public DevicePath Path { get; }
}

/// <summary>The device is known to ADB but not responding.</summary>
public sealed class DeviceOfflineException : DeviceException
{
    public DeviceOfflineException(string? technicalDetail = null, Exception? inner = null)
        : base("The device is connected but not responding. Try reconnecting the cable.",
            technicalDetail,
            inner)
    {
    }

    public override bool IsTransient => true;
}

/// <summary>The user has not accepted the ADB authorization prompt (spec §41).</summary>
public sealed class DeviceUnauthorizedException : DeviceException
{
    public DeviceUnauthorizedException(string? technicalDetail = null, Exception? inner = null)
        : base("This computer is not authorized on the device yet. Unlock the phone and tap "
               + "\"Allow\" on the USB debugging prompt.",
            technicalDetail,
            inner)
    {
    }
}

/// <summary>The ADB server could not be started, found or reached (spec §72).</summary>
public sealed class AdbServerException : DeviceException
{
    public AdbServerException(string userMessage, string? technicalDetail = null, Exception? inner = null)
        : base(userMessage, technicalDetail, inner)
    {
    }

    public static AdbServerException NotFound() => new(
        "Could not find the Android connection tool (adb). Set its location in Settings, or let "
        + "Android Explorer download it for you.");

    public static AdbServerException StartFailed(string? detail = null) => new(
        "Could not start the Android connection service.", detail);

    public override bool IsTransient => true;
}

/// <summary>The destination ran out of space (spec §81).</summary>
public sealed class InsufficientSpaceException : DeviceException
{
    public InsufficientSpaceException(long requiredBytes, long availableBytes, bool onDevice)
        : base(onDevice
                ? "Not enough free space on the device to finish this transfer."
                : "Not enough free space on this computer to finish this transfer.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public long RequiredBytes { get; }

    public long AvailableBytes { get; }
}

/// <summary>Shared storage is unavailable — typically the phone is locked (spec §81).</summary>
public sealed class DeviceStorageUnavailableException : DeviceException
{
    public DeviceStorageUnavailableException(string? technicalDetail = null, Exception? inner = null)
        : base("The device's storage is unavailable. Unlock the phone and try again.",
            technicalDetail,
            inner)
    {
    }

    public override bool IsTransient => true;
}

/// <summary>The device does not support the requested operation (spec §77).</summary>
public sealed class CapabilityNotSupportedException : DeviceException
{
    public CapabilityNotSupportedException(string capability)
        : base("This device does not support that operation.", $"missing capability: {capability}")
        => Capability = capability;

    public string Capability { get; }
}
