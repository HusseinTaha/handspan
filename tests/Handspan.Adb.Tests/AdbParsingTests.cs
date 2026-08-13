using Handspan.Core.Models;

namespace Handspan.Adb.Tests;

/// <summary>Parsing of the ADB server's device list and state words (spec §5, §38).</summary>
public class AdbDeviceListParsingTests
{
    [Fact]
    public void Parses_the_short_tracking_format()
    {
        // host:track-devices sends "serial\tstate\n" per device.
        var payload = "R5CX309PAEB\tdevice\n192.168.1.42:5555\tdevice\n";

        var entries = AdbHostClient.ParseDeviceList(payload);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new DeviceId("R5CX309PAEB"), entries[0].Id);
        Assert.Equal(DeviceState.Online, entries[0].State);
        Assert.True(entries[1].Id.IsWireless);
    }

    [Fact]
    public void Parses_the_long_format_with_trailing_fields()
    {
        var payload = "R5CX309PAEB            device product:e1q model:SM_S928B device:e1q transport_id:1\n";

        var entries = AdbHostClient.ParseDeviceList(payload);

        var entry = Assert.Single(entries);
        Assert.Equal(new DeviceId("R5CX309PAEB"), entry.Id);
        Assert.Equal(DeviceState.Online, entry.State);
    }

    [Fact]
    public void Parses_an_unauthorized_device()
    {
        var entries = AdbHostClient.ParseDeviceList("R5CX309PAEB            unauthorized transport_id:1\n");

        Assert.Equal(DeviceState.Unauthorized, Assert.Single(entries).State);
    }

    [Fact]
    public void An_empty_payload_means_no_devices()
    {
        // This is how the server reports the last device being unplugged, so it must not throw.
        Assert.Empty(AdbHostClient.ParseDeviceList(string.Empty));
        Assert.Empty(AdbHostClient.ParseDeviceList("\n"));
        Assert.Empty(AdbHostClient.ParseDeviceList("   "));
    }

    [Fact]
    public void Skips_the_cli_header_and_malformed_lines()
    {
        var payload = "List of devices attached\nR5CX309PAEB\tdevice\ngarbage\n";

        Assert.Single(AdbHostClient.ParseDeviceList(payload));
    }

    [Theory]
    [InlineData("device", DeviceState.Online)]
    [InlineData("unauthorized", DeviceState.Unauthorized)]
    [InlineData("authorizing", DeviceState.Unauthorized)]
    [InlineData("offline", DeviceState.Offline)]
    [InlineData("bootloader", DeviceState.Unknown)]
    [InlineData("recovery", DeviceState.Unknown)]
    [InlineData("sideload", DeviceState.Unknown)]
    [InlineData("no", DeviceState.Unknown)]
    public void Maps_state_words(string word, DeviceState expected)
        => Assert.Equal(expected, AdbHostClient.ParseState(word));
}

/// <summary>Mapping protocol failures onto explanations the user can act on (spec §48).</summary>
public class AdbFailureTranslationTests
{
    [Fact]
    public void Unauthorized_becomes_the_authorization_prompt_guidance()
    {
        var exception = AdbFailure.Translate("device unauthorized. Please check the confirmation dialog");

        var typed = Assert.IsType<Core.Exceptions.DeviceUnauthorizedException>(exception);
        Assert.Contains("Allow", typed.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Permission_denied_names_the_path_and_blames_android()
    {
        var path = DevicePath.Parse("/data/data");

        var exception = AdbFailure.Translate("permission denied", path);

        var typed = Assert.IsType<Core.Exceptions.AccessDeniedException>(exception);
        Assert.Equal(path, typed.Path);
        Assert.Contains("protected by Android", typed.UserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("device offline", typeof(Core.Exceptions.DeviceOfflineException))]
    [InlineData("device not found", typeof(Core.Exceptions.DeviceDisconnectedException))]
    [InlineData("no such file or directory", typeof(Core.Exceptions.PathNotFoundException))]
    [InlineData("no space left on device", typeof(Core.Exceptions.InsufficientSpaceException))]
    public void Maps_known_failures(string message, Type expected)
        => Assert.IsType(expected, AdbFailure.Translate(message));

    [Fact]
    public void Unrecognized_failures_stay_generic_but_never_leak_to_the_user_as_protocol_text()
    {
        var exception = AdbFailure.Translate("something entirely new");

        Assert.IsType<AdbProtocolException>(exception);

        // The raw text is kept for diagnostics only; the user-facing message is plain English.
        Assert.Equal("something entirely new", exception.TechnicalDetail);
        Assert.DoesNotContain("something entirely new", exception.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_failures_are_marked_retryable()
    {
        // The transfer manager keys its retry decisions off this (spec §11).
        Assert.True(AdbFailure.Translate("device offline").IsTransient);
        Assert.True(AdbFailure.Translate("device not found").IsTransient);
        Assert.False(AdbFailure.Translate("permission denied").IsTransient);
    }
}

/// <summary>POSIX mode decoding, which decides what a listing entry actually is.</summary>
public class PosixModeTests
{
    [Theory]
    [InlineData(0x4000 | 0x1ED, DeviceEntryKind.Directory)] // drwxr-xr-x
    [InlineData(0x8000 | 0x1A4, DeviceEntryKind.File)]      // -rw-r--r--
    [InlineData(0xA000 | 0x1FF, DeviceEntryKind.Symlink)]   // lrwxrwxrwx, e.g. /sdcard
    [InlineData(0xC000, DeviceEntryKind.Other)]             // socket
    [InlineData(0x1000, DeviceEntryKind.Other)]             // fifo
    public void Classifies_entries_by_type_bits(int mode, DeviceEntryKind expected)
        => Assert.Equal(expected, PosixMode.ToKind(mode));

    [Fact]
    public void Permission_bits_are_isolated_from_type_bits()
    {
        const int mode = 0x8000 | 0x1A4;

        Assert.Equal(0x1A4, mode & PosixMode.PermissionMask);
    }
}
