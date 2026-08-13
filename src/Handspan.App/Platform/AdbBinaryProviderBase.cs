using System.IO.Compression;
using Handspan.Core.Exceptions;
using Handspan.Core.Platform;

namespace Handspan.App.Platform;

/// <summary>
/// Shared adb discovery and provisioning logic (spec §4.1, §72).
/// </summary>
/// <remarks>
/// Search order: bundled copy, PATH, ANDROID_HOME/ANDROID_SDK_ROOT, the platform's usual SDK
/// locations, then the user-configured path. Downloading is only ever done with explicit user
/// consent, because the app must work fully offline afterwards (spec §44).
/// </remarks>
internal abstract class AdbBinaryProviderBase : IAdbBinaryProvider
{
    /// <summary>Executable name, "adb.exe" or "adb".</summary>
    protected abstract string ExecutableName { get; }

    /// <summary>Platform-tools archive for this OS, from Google's official download host.</summary>
    protected abstract string PlatformToolsArchiveUrl { get; }

    /// <summary>Locations to search after PATH and the SDK environment variables.</summary>
    protected abstract IEnumerable<string> CandidateDirectories { get; }

    /// <summary>Per-user application data directory.</summary>
    protected abstract string AppDataFolder { get; }

    /// <summary>Path the user set explicitly in settings, checked last so it can override nothing.</summary>
    public string? ConfiguredPath { get; set; }

    public Task<string?> LocateAsync(CancellationToken cancellationToken)
        => Task.Run(Locate, cancellationToken);

    public async Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(AppDataFolder, "platform-tools");
        var archivePath = Path.Combine(Path.GetTempPath(), $"platform-tools-{Guid.NewGuid():N}.zip");

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await http
                       .GetAsync(PlatformToolsArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destination = File.Create(archivePath);

                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;

                    if (total is > 0)
                    {
                        progress?.Report((double)copied / total.Value);
                    }
                }
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            // The archive contains a top-level "platform-tools" directory.
            Directory.CreateDirectory(AppDataFolder);
            ZipFile.ExtractToDirectory(archivePath, AppDataFolder, overwriteFiles: true);

            var executable = Path.Combine(targetDirectory, ExecutableName);
            if (!File.Exists(executable))
            {
                throw AdbServerException.StartFailed(
                    $"downloaded archive did not contain {ExecutableName}");
            }

            await PrepareForExecutionAsync(executable, cancellationToken).ConfigureAwait(false);
            return executable;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    public virtual Task PrepareForExecutionAsync(string path, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private string? Locate()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string?> EnumerateCandidates()
    {
        // 1. Bundled alongside the application.
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools", ExecutableName);

        // 2. Previously downloaded into app data.
        yield return Path.Combine(AppDataFolder, "platform-tools", ExecutableName);

        // 3. PATH.
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory.Trim(), ExecutableName);
        }

        // 4. Android SDK environment variables.
        foreach (var variable in (string[])["ANDROID_HOME", "ANDROID_SDK_ROOT"])
        {
            var sdk = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(sdk))
            {
                yield return Path.Combine(sdk, "platform-tools", ExecutableName);
            }
        }

        // 5. Platform-specific well-known locations.
        foreach (var directory in CandidateDirectories)
        {
            yield return Path.Combine(directory, ExecutableName);
        }

        // 6. Whatever the user configured in settings.
        yield return ConfiguredPath;
    }
}
