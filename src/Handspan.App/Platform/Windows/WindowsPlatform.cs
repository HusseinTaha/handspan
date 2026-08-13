using System.Diagnostics;
using Handspan.Core.Platform;

namespace Handspan.App.Platform.Windows;

internal sealed class WindowsAdbBinaryProvider : AdbBinaryProviderBase
{
    protected override string ExecutableName => "adb.exe";

    protected override string PlatformToolsArchiveUrl
        => "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    protected override string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Handspan");

    protected override IEnumerable<string> CandidateDirectories
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools");
            yield return Path.Combine(programFiles, "Android", "android-sdk", "platform-tools");
            yield return @"C:\platform-tools";
            yield return @"C:\adb";
        }
    }
}

internal sealed class WindowsShellIntegration : IShellIntegration
{
    public Task RevealInFileManagerAsync(string localPath)
    {
        // /select, needs the target quoted but not the switch.
        Start("explorer.exe", $"/select,\"{localPath}\"");
        return Task.CompletedTask;
    }

    public Task OpenAsync(string localPath)
    {
        Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }

    public Task OpenWithAsync(string localPath)
    {
        Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {localPath}");
        return Task.CompletedTask;
    }

    public string GetDefaultDownloadFolder()
    {
        // Windows has no SpecialFolder for Downloads; the shell known folder is the correct source,
        // but the profile-relative path is right on every supported version and needs no interop.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }

    public string GetAppDataFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Handspan");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Start(string fileName, string arguments)
        => Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })?.Dispose();
}
