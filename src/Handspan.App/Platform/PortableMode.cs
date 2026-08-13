namespace Handspan.App.Platform;

/// <summary>
/// Where a portable build keeps the things it writes (docs/plan/08-packaging.md).
/// </summary>
/// <remarks>
/// <para>
/// A portable build is expected to keep everything inside its own folder, so it can live on a USB stick,
/// be run on a machine that is not the user's, and leave nothing behind. A marker file named
/// <c>Handspan.portable</c> beside the executable switches every writable location — settings, the SQLite
/// databases, the thumbnail cache, logs, and a downloaded copy of adb — into a <c>Data</c> subdirectory
/// there. Only the marker's presence matters, never its contents, so deleting it returns the app to the
/// ordinary per-user location and the file itself is free to explain what it does.
/// </para>
/// <para>
/// The marker being present does not make the folder writable: a portable build unzipped under Program
/// Files, or run from a write-protected stick, cannot store anything next to itself. That case falls back
/// to the per-user folder rather than refusing to start — an app that will not launch is a worse outcome
/// than one whose data did not travel with it — and records why. <see cref="FallbackReason"/> is surfaced
/// in the UI next to the folder actually in use, so the fallback is visible rather than merely silent.
/// </para>
/// <para>
/// Windows only, by choice rather than by <c>#if</c>: this is a convention of Windows zip distribution.
/// A macOS <c>.app</c> is already drag-to-install, its executable lives inside the bundle where a marker
/// file has no natural home, and Gatekeeper may relocate the bundle to a read-only path before it runs.
/// </para>
/// </remarks>
internal static class PortableMode
{
    /// <summary>Marker file that enables portable mode. Shipped in the portable zip.</summary>
    public const string MarkerFileName = "Handspan.portable";

    /// <summary>Subdirectory holding everything a portable instance writes.</summary>
    public const string DataDirectoryName = "Data";

    private static Location? _resolved;

    /// <summary>True when data is being kept beside the executable.</summary>
    /// <remarks>
    /// False before anything has asked for a data folder, and on any platform that does not consult this
    /// at all — which is why these two report rather than demand that resolution has happened. Throwing
    /// would make a macOS launch depend on a Windows-only code path having run first.
    /// </remarks>
    public static bool IsEnabled => _resolved?.IsPortable ?? false;

    /// <summary>Why portable mode was requested but not used, or null. Contains no paths (spec §43).</summary>
    public static string? FallbackReason => _resolved?.FallbackReason;

    /// <summary>The folder to store data in, given where this platform would otherwise put it.</summary>
    public static string ResolveAppDataFolder(string perUserFolder)
    {
        // Cached because it probes the filesystem and is asked for by several services during startup.
        // Race-benign: two threads resolving concurrently reach the same answer.
        _resolved ??= Resolve(AppContext.BaseDirectory, perUserFolder);
        return _resolved.Folder;
    }

    /// <summary>
    /// The decision itself, separated from where the executable happens to live so it can be tested.
    /// </summary>
    internal static Location Resolve(string executableDirectory, string perUserFolder)
    {
        if (!File.Exists(Path.Combine(executableDirectory, MarkerFileName)))
        {
            return new Location(perUserFolder, IsPortable: false, FallbackReason: null);
        }

        var data = Path.Combine(executableDirectory, DataDirectoryName);

        try
        {
            // Creating the directory is not proof of writability on Windows: a folder can be created by an
            // administrator's earlier run and still refuse this user's writes. So write something.
            Directory.CreateDirectory(data);

            var probe = Path.Combine(data, ".write-probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);

            return new Location(data, IsPortable: true, FallbackReason: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Deliberately path-free: this string reaches the log file, and logs must not carry paths (§43).
            return new Location(
                perUserFolder,
                IsPortable: false,
                FallbackReason: "the portable Data folder beside the application is not writable, "
                                + "so per-user application data is being used instead");
        }
    }

    /// <summary>Resets the cached decision. Tests only.</summary>
    internal static void Reset() => _resolved = null;

    internal sealed record Location(string Folder, bool IsPortable, string? FallbackReason);
}
