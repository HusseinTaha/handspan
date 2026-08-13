using Handspan.App.Platform;

namespace Handspan.Services.Tests;

/// <summary>
/// Where a portable build keeps its data (docs/plan/08-packaging.md).
/// </summary>
/// <remarks>
/// The point of the portable build is that it writes nothing to the machine it runs on, so getting this
/// wrong is not a cosmetic bug: it silently scatters settings, caches and logs into the user's profile on
/// a PC that may not be theirs. Tested against real directories rather than a filesystem mock, because the
/// decision hinges on what the filesystem actually permits.
/// </remarks>
public sealed class PortableModeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "handspan-portable-tests", Guid.NewGuid().ToString("N"));

    private readonly string _perUser;
    private readonly string _executableDirectory;

    public PortableModeTests()
    {
        _perUser = Path.Combine(_root, "per-user");
        _executableDirectory = Path.Combine(_root, "app");

        Directory.CreateDirectory(_perUser);
        Directory.CreateDirectory(_executableDirectory);
    }

    [Fact]
    public void Without_the_marker_the_ordinary_per_user_folder_is_used()
    {
        var location = PortableMode.Resolve(_executableDirectory, _perUser);

        Assert.False(location.IsPortable);
        Assert.Equal(_perUser, location.Folder);
        Assert.Null(location.FallbackReason);

        // An installed build must not create anything beside itself; Program Files is not writable.
        Assert.False(Directory.Exists(Path.Combine(_executableDirectory, PortableMode.DataDirectoryName)));
    }

    [Fact]
    public void The_marker_moves_data_beside_the_executable()
    {
        WriteMarker();

        var location = PortableMode.Resolve(_executableDirectory, _perUser);

        Assert.True(location.IsPortable);
        Assert.Equal(
            Path.Combine(_executableDirectory, PortableMode.DataDirectoryName), location.Folder);
        Assert.Null(location.FallbackReason);
        Assert.True(Directory.Exists(location.Folder));
    }

    [Fact]
    public void The_marker_is_a_marker_and_its_contents_are_irrelevant()
    {
        // It ships with text explaining itself, so anything inside it must be ignored.
        File.WriteAllText(
            Path.Combine(_executableDirectory, PortableMode.MarkerFileName),
            "Delete this file to store data in your user profile instead.\r\n");

        Assert.True(PortableMode.Resolve(_executableDirectory, _perUser).IsPortable);
    }

    [Fact]
    public void An_unwritable_portable_folder_falls_back_instead_of_failing_to_start()
    {
        WriteMarker();

        // Occupying the name with a file is a deterministic stand-in for the real cases — unzipped under
        // Program Files, or run from a write-protected stick — which cannot be created on demand here.
        File.WriteAllText(Path.Combine(_executableDirectory, PortableMode.DataDirectoryName), "not a folder");

        var location = PortableMode.Resolve(_executableDirectory, _perUser);

        Assert.False(location.IsPortable);
        Assert.Equal(_perUser, location.Folder);
        Assert.NotNull(location.FallbackReason);
    }

    [Fact]
    public void The_fallback_reason_carries_no_path()
    {
        WriteMarker();
        File.WriteAllText(Path.Combine(_executableDirectory, PortableMode.DataDirectoryName), "not a folder");

        var reason = PortableMode.Resolve(_executableDirectory, _perUser).FallbackReason;

        // It is logged, and logs must not contain paths or filenames (spec §43). An exception message
        // would have included the full path, which is exactly why the reason is written by hand.
        Assert.NotNull(reason);
        Assert.DoesNotContain(_root, reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, reason);
        Assert.DoesNotContain(':', reason);
    }

    /// <summary>
    /// A Data folder that already exists but cannot be written to still has to fall back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case that makes the write probe necessary rather than paranoid. <c>CreateDirectory</c>
    /// succeeding says nothing about writability: a Data folder left behind by an elevated run, restored
    /// from a backup, or sitting on a stick whose write-protect switch is on exists perfectly happily and
    /// then refuses every write. Trusting <c>CreateDirectory</c> alone would report portable mode as
    /// active and then fail on the first setting saved.
    /// </para>
    /// <para>
    /// The obstruction here is a <em>directory</em> occupying the probe's name, which makes the probe write
    /// fail while directory creation succeeds — the one way to produce that combination that behaves the
    /// same on every machine. An ACL would be the realistic cause, but denying yourself write access is
    /// not reliably permitted, and the first version of this test quietly skipped itself and passed
    /// without exercising anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_existing_but_unwritable_folder_falls_back_too()
    {
        WriteMarker();

        var data = Path.Combine(_executableDirectory, PortableMode.DataDirectoryName);
        Directory.CreateDirectory(Path.Combine(data, ".write-probe"));

        var location = PortableMode.Resolve(_executableDirectory, _perUser);

        Assert.True(Directory.Exists(data), "the folder exists, so only the write probe can have caught this");
        Assert.False(location.IsPortable);
        Assert.Equal(_perUser, location.Folder);
        Assert.NotNull(location.FallbackReason);
    }

    private void WriteMarker()
        => File.WriteAllBytes(Path.Combine(_executableDirectory, PortableMode.MarkerFileName), []);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
