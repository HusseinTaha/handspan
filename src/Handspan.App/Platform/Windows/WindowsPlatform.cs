using System.Diagnostics;
using Handspan.Core.Platform;

namespace Handspan.App.Platform.Windows;

/// <summary>Where this app writes on Windows. One place, because two answers would eventually differ.</summary>
internal static class WindowsPaths
{
    /// <summary>The ordinary location, used unless the portable marker says otherwise.</summary>
    public static string PerUser => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Handspan");

    /// <summary>Per-user, or beside the executable for a portable build (<see cref="PortableMode"/>).</summary>
    public static string AppData => PortableMode.ResolveAppDataFolder(PerUser);
}

internal sealed class WindowsAdbBinaryProvider : AdbBinaryProviderBase
{
    protected override string ExecutableName => "adb.exe";

    protected override string PlatformToolsArchiveUrl
        => "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    // A downloaded adb belongs with the rest of the data, so a portable copy stays self-contained
    // after its first run rather than depending on the machine it was first used on.
    protected override string AppDataFolder => WindowsPaths.AppData;

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

            // An adb that an ordinary (non-portable) run of this app downloaded earlier. Only reachable
            // from a portable copy, where AppDataFolder points at its own Data folder instead — without
            // this, running the portable build on a machine that already has Handspan re-downloads
            // platform-tools for no reason. It stays last: nothing here is preferred over a real SDK, and
            // when nothing is found at all the portable copy still downloads into its own folder.
            yield return Path.Combine(WindowsPaths.PerUser, "platform-tools");
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
        var folder = WindowsPaths.AppData;
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Start(string fileName, string arguments)
        => Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })?.Dispose();
}
