using System.Diagnostics;
using AndroidExplorer.Adb;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Adb.Tests;

/// <summary>
/// Discovers whether this machine can run live ADB tests, and establishes ground truth using the adb
/// CLI so our own client can be checked against it.
/// </summary>
/// <remarks>
/// Using the CLI here is deliberate: the point of these tests is to prove our hand-written protocol
/// implementation agrees with Google's, so the CLI is the reference, not the implementation.
/// </remarks>
internal static class AdbTestEnvironment
{
    private static readonly Lazy<string?> LazyAdbPath = new(FindAdb);

    private static readonly Lazy<IReadOnlyList<(string Serial, string State)>> LazyDevices =
        new(QueryDevicesFromCli);

    public static string? AdbPath => LazyAdbPath.Value;

    public static IReadOnlyList<(string Serial, string State)> CliDevices => LazyDevices.Value;

    public static bool HasOnlineDevice => CliDevices.Any(device => device.State == "device");

    /// <summary>Builds a service provider wired to the real transport.</summary>
    public static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IAdbBinaryProvider>(new FixedAdbBinaryProvider(AdbPath!));
        services.AddAdbTransport();
        return services.BuildServiceProvider();
    }

    private static string? FindAdb()
    {
        // CI runners ship an adb of their own but never a phone. Finding it there would start a real
        // adb daemon on the runner to prove nothing, so an explicit opt-out keeps CI hermetic.
        if (Environment.GetEnvironmentVariable("ANDROIDEXPLORER_NO_DEVICE_TESTS") is { Length: > 0 })
        {
            return null;
        }

        var candidates = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AndroidExplorer", "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Android", "sdk", "platform-tools", "adb"),
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(
                directory.Trim(), OperatingSystem.IsWindows() ? "adb.exe" : "adb")));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<(string, string)> QueryDevicesFromCli()
    {
        if (AdbPath is null)
        {
            return [];
        }

        var startInfo = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("devices");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return [];
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(20_000);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("List of devices", StringComparison.Ordinal))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2)
            .Select(fields => (fields[0], fields[1]))
            .ToList();
    }

    private sealed class FixedAdbBinaryProvider(string path) : IAdbBinaryProvider
    {
        public Task<string?> LocateAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(path);

        public Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException("Tests never download adb.");

        public Task PrepareForExecutionAsync(string path, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

/// <summary>Runs only when an adb binary is present.</summary>
public sealed class RequiresAdbFactAttribute : FactAttribute
{
    public RequiresAdbFactAttribute()
    {
        if (AdbTestEnvironment.AdbPath is null)
        {
            Skip = "No adb binary on this machine.";
        }
    }
}

/// <summary>Runs only when a device is connected, in any state.</summary>
public sealed class RequiresDeviceFactAttribute : FactAttribute
{
    public RequiresDeviceFactAttribute()
    {
        if (AdbTestEnvironment.AdbPath is null)
        {
            Skip = "No adb binary on this machine.";
        }
        else if (AdbTestEnvironment.CliDevices.Count == 0)
        {
            Skip = "No Android device connected.";
        }
    }
}

/// <summary>Runs only when a device is connected and has authorized this computer.</summary>
public sealed class RequiresOnlineDeviceFactAttribute : FactAttribute
{
    public RequiresOnlineDeviceFactAttribute()
    {
        if (AdbTestEnvironment.AdbPath is null)
        {
            Skip = "No adb binary on this machine.";
        }
        else if (!AdbTestEnvironment.HasOnlineDevice)
        {
            Skip = AdbTestEnvironment.CliDevices.Count == 0
                ? "No Android device connected."
                : "Device is connected but has not authorized this computer (accept the prompt on the phone).";
        }
    }
}
