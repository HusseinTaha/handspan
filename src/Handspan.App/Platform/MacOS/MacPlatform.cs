using System.Diagnostics;
using Handspan.Core.Platform;

namespace Handspan.App.Platform.MacOS;

internal sealed class MacAdbBinaryProvider : AdbBinaryProviderBase
{
    protected override string ExecutableName => "adb";

    protected override string PlatformToolsArchiveUrl
        => "https://dl.google.com/android/repository/platform-tools-latest-darwin.zip";

    protected override string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support",
        "Handspan");

    protected override IEnumerable<string> CandidateDirectories
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(home, "Library", "Android", "sdk", "platform-tools");
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/opt/local/bin";
        }
    }

    /// <summary>
    /// A downloaded binary is quarantined and non-executable on macOS; without both fixes it fails
    /// to launch with an unhelpful error.
    /// </summary>
    public override async Task PrepareForExecutionAsync(string path, CancellationToken cancellationToken)
    {
        await RunAsync("/bin/chmod", ["+x", path], cancellationToken).ConfigureAwait(false);
        await RunAsync("/usr/bin/xattr", ["-d", "com.apple.quarantine", path], cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        // xattr exits non-zero when the attribute is absent, which is a normal case, so the exit
        // code is deliberately ignored here.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class MacShellIntegration : IShellIntegration
{
    public Task RevealInFileManagerAsync(string localPath)
    {
        Start("/usr/bin/open", ["-R", localPath]);
        return Task.CompletedTask;
    }

    public Task OpenAsync(string localPath)
    {
        Start("/usr/bin/open", [localPath]);
        return Task.CompletedTask;
    }

    public Task OpenWithAsync(string localPath)
    {
        // macOS has no direct "open with" chooser from the command line; revealing the file lets the
        // user use Finder's own chooser. Phase 6 can replace this with an NSWorkspace call.
        Start("/usr/bin/open", ["-R", localPath]);
        return Task.CompletedTask;
    }

    public string GetDefaultDownloadFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }

    public string GetAppDataFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Handspan");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Start(string fileName, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo)?.Dispose();
    }
}
