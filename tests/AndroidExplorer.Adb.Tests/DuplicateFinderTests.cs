using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Data;
using AndroidExplorer.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace AndroidExplorer.Adb.Tests;

/// <summary>
/// Duplicate detection against the fake device (spec §61).
/// </summary>
/// <remarks>
/// The design claim being tested is the <em>cost ordering</em>: size grouping is free, the head/tail sample is
/// cheap, and full hashes are expensive and must therefore run only on candidates that survive the cheaper
/// passes. Asserting the number of <c>sha256sum</c> commands is how that claim is held to account.
/// </remarks>
public sealed class DuplicateFinderTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ae-dupes-{Guid.NewGuid():N}.db");

    private FakeServerFixture _fixture = null!;
    private IFileIndexStore _index = null!;
    private IDeviceFileSystem _fileSystem = null!;

    public Task InitializeAsync()
    {
        _fixture = FakeServerFixture.Start();
        _fileSystem = _fixture.CreateFileSystem();

        var database = new AndroidExplorerDatabase(
            _databasePath, NullLogger<AndroidExplorerDatabase>.Instance);
        _index = new SqliteFileIndexStore(database, NullLogger<SqliteFileIndexStore>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(_databasePath + suffix))
                {
                    File.Delete(_databasePath + suffix);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private IDuplicateFinder CreateFinder()
        => new DuplicateFinder(
            _fixture.Device, _fileSystem, _index, NullLogger<DuplicateFinder>.Instance);

    /// <summary>Places a file on the fake device and indexes it, as a crawl would have.</summary>
    private async Task PlaceAsync(string name, byte[] content)
    {
        var remote = $"/storage/emulated/0/Download/{name}";
        _fixture.Server.Files.AddFile(remote, content);

        await _index.UpsertBatchAsync(_fixture.Device, [new DeviceEntry
        {
            DeviceId = _fixture.Device,
            Path = KnownPaths.Download.Combine(name),
            Kind = DeviceEntryKind.File,
            Size = content.Length,
            Modified = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000),
        }], CancellationToken.None);
    }

    private static byte[] Content(int size, byte seed)
    {
        var content = new byte[size];
        for (var i = 0; i < size; i++)
        {
            content[i] = (byte)((i * 31 + seed) % 251);
        }

        return content;
    }

    private int Sha256CommandCount()
    {
        lock (_fixture.Server.ExecutedCommands)
        {
            return _fixture.Server.ExecutedCommands
                .Count(command => command.StartsWith("sha256sum", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Finds_identical_files_and_reports_reclaimable_space()
    {
        var content = Content(300_000, 7);

        await PlaceAsync("photo.jpg", content);
        await PlaceAsync("photo-copy.jpg", content);
        await PlaceAsync("photo (1).jpg", content);
        await PlaceAsync("different.jpg", Content(300_001, 9));

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024 }, null, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Paths.Count);
        Assert.Equal(DuplicateConfidence.PartialHash, group.Confidence);

        // Keeping one copy of three identical files reclaims two copies.
        Assert.Equal(600_000, group.ReclaimableBytes);
    }

    [Fact]
    public async Task Files_sharing_a_size_but_not_content_are_not_duplicates()
    {
        // Same length, different bytes — exactly the coincidence the sample pass exists to reject.
        await PlaceAsync("a.bin", Content(200_000, 1));
        await PlaceAsync("b.bin", Content(200_000, 2));

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024 }, null, CancellationToken.None);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Files_sharing_a_header_but_differing_later_are_separated()
    {
        // Same camera, same container: identical first 64 KB, different tails. Sampling only the head
        // would group these wrongly, which is why the tail is sampled too.
        var shared = Content(400_000, 3);

        var first = shared.ToArray();
        var second = shared.ToArray();
        for (var i = 300_000; i < second.Length; i++)
        {
            second[i] = 0xAB;
        }

        await PlaceAsync("clip-a.mp4", first);
        await PlaceAsync("clip-b.mp4", second);

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024 }, null, CancellationToken.None);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Full_hashing_is_skipped_unless_asked_for()
    {
        var content = Content(150_000, 5);
        await PlaceAsync("one.bin", content);
        await PlaceAsync("two.bin", content);

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024, VerifyWithFullHash = false },
            null,
            CancellationToken.None);

        Assert.Single(groups);

        // The expensive pass must not have run at all: hashing whole files over USB is the thing the cost
        // ordering exists to avoid (spec §36, §61).
        Assert.Equal(0, Sha256CommandCount());
    }

    [Fact]
    public async Task Full_hashing_confirms_only_the_surviving_candidates()
    {
        var duplicated = Content(150_000, 5);

        await PlaceAsync("dup-a.bin", duplicated);
        await PlaceAsync("dup-b.bin", duplicated);

        // Two more files that share a size with each other but differ in content: they must be eliminated
        // by the sample pass and never reach the hasher.
        await PlaceAsync("coincidence-a.bin", Content(90_000, 1));
        await PlaceAsync("coincidence-b.bin", Content(90_000, 2));

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024, VerifyWithFullHash = true },
            null,
            CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(DuplicateConfidence.FullHash, group.Confidence);
        Assert.Equal(2, group.Paths.Count);

        // Exactly the two survivors were hashed — not the coincidental pair, and not every indexed file.
        Assert.Equal(2, Sha256CommandCount());
    }

    [Fact]
    public async Task Small_files_are_ignored()
    {
        var tiny = Content(500, 4);
        await PlaceAsync("tiny-a.txt", tiny);
        await PlaceAsync("tiny-b.txt", tiny);

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 64 * 1024 }, null, CancellationToken.None);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task A_device_without_sha256sum_falls_back_to_the_partial_verdict()
    {
        _fixture.Server.Faults.NoSha256Sum = true;

        var content = Content(150_000, 6);
        await PlaceAsync("x.bin", content);
        await PlaceAsync("y.bin", content);

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024, VerifyWithFullHash = true },
            null,
            CancellationToken.None);

        // The group survives, honestly labelled as unconfirmed rather than dropped or overstated.
        var group = Assert.Single(groups);
        Assert.Equal(DuplicateConfidence.PartialHash, group.Confidence);
    }

    [Fact]
    public async Task Groups_are_ordered_by_how_much_space_they_would_free()
    {
        var big = Content(500_000, 1);
        var small = Content(120_000, 2);

        await PlaceAsync("big-a.bin", big);
        await PlaceAsync("big-b.bin", big);
        await PlaceAsync("small-a.bin", small);
        await PlaceAsync("small-b.bin", small);

        var groups = await CreateFinder().FindAsync(
            new DuplicateSearchOptions { MinimumBytes = 1024 }, null, CancellationToken.None);

        Assert.Equal(2, groups.Count);
        Assert.True(groups[0].ReclaimableBytes > groups[1].ReclaimableBytes);
    }
}
