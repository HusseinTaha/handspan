using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using AndroidExplorer.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace AndroidExplorer.Services.Tests;

/// <summary>
/// The search index and its FTS5 behaviour (spec §27, §28).
/// </summary>
/// <remarks>
/// Run against real SQLite, because the interesting questions are about the FTS tokenizer — whether Arabic,
/// CJK and accented names match — and a mock would answer none of them.
/// </remarks>
public sealed class FileIndexTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ae-index-{Guid.NewGuid():N}.db");

    private static readonly DeviceId Device = new("indexDevice");
    private static readonly DeviceId OtherDevice = new("otherDevice");

    private IFileIndexStore _index = null!;

    public Task InitializeAsync()
    {
        var database = new AndroidExplorerDatabase(
            _databasePath, NullLogger<AndroidExplorerDatabase>.Instance);

        _index = new SqliteFileIndexStore(database, NullLogger<SqliteFileIndexStore>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
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

        return Task.CompletedTask;
    }

    private static DeviceEntry Entry(
        string name,
        DevicePath? parent = null,
        long size = 4096,
        long modifiedUnix = 1_760_000_000,
        DeviceId? device = null) => new()
    {
        DeviceId = device ?? Device,
        Path = (parent ?? KnownPaths.Download).Combine(name),
        Kind = DeviceEntryKind.File,
        Size = size,
        Modified = DateTimeOffset.FromUnixTimeSeconds(modifiedUnix),
    };

    private async Task IndexAsync(params DeviceEntry[] entries)
        => await _index.UpsertBatchAsync(
            entries[0].DeviceId, entries, CancellationToken.None);

    private Task<IReadOnlyList<DeviceEntry>> SearchAsync(string text, SearchQuery? template = null)
        => _index.SearchAsync(
            Device,
            (template ?? new SearchQuery { Text = text }) with { Text = text },
            CancellationToken.None);

    [Fact]
    public async Task Finds_files_by_a_whole_token()
    {
        await IndexAsync(
            Entry("invoice.pdf"),
            Entry("invoice-2026.pdf"),
            Entry("old-invoice.jpg"),
            Entry("holiday.jpg"));

        var results = await SearchAsync("invoice");

        // The tokenizer splits on '-', so all three forms match — including the one where "invoice" is not
        // the first token.
        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(results, entry => entry.Name == "holiday.jpg");
    }

    [Fact]
    public async Task Matches_a_prefix_while_the_user_is_still_typing()
    {
        await IndexAsync(Entry("presentation.pptx"), Entry("photo.jpg"));

        Assert.Contains(await SearchAsync("presen"), entry => entry.Name == "presentation.pptx");
        Assert.Contains(await SearchAsync("pho"), entry => entry.Name == "photo.jpg");
    }

    [Fact]
    public async Task Matches_inside_a_token_through_the_substring_pass()
    {
        await IndexAsync(Entry("invoice.pdf"));

        // FTS alone cannot match mid-token; the LIKE fallback is what makes this work.
        Assert.Single(await SearchAsync("voice"));
    }

    [Theory]
    [InlineData("صور", "صور العائلة.jpg")]
    [InlineData("العائلة", "صور العائلة.jpg")]
    [InlineData("照片", "照片.png")]
    [InlineData("旅行", "旅行 🌴.mp4")]
    [InlineData("한국어", "한국어.txt")]
    public async Task Finds_non_latin_filenames(string query, string expected)
    {
        await IndexAsync(
            Entry("صور العائلة.jpg"),
            Entry("照片.png"),
            Entry("旅行 🌴.mp4"),
            Entry("한국어.txt"));

        Assert.Contains(await SearchAsync(query), entry => entry.Name == expected);
    }

    [Fact]
    public async Task Diacritics_are_folded_so_accents_are_optional()
    {
        await IndexAsync(Entry("résumé.pdf"), Entry("café menu.txt"));

        // remove_diacritics in the tokenizer is what makes this work; without it a user typing plain ASCII
        // would never find their own files.
        Assert.Contains(await SearchAsync("resume"), entry => entry.Name == "résumé.pdf");
        Assert.Contains(await SearchAsync("cafe"), entry => entry.Name == "café menu.txt");
    }

    [Fact]
    public async Task Punctuation_in_a_query_cannot_break_the_match_syntax()
    {
        await IndexAsync(Entry("report (final).pdf"), Entry("a\"quote\".txt"));

        // FTS5 treats quotes and parentheses as syntax; unescaped they would throw rather than search.
        var results = await SearchAsync("report (final)");
        Assert.Contains(results, entry => entry.Name == "report (final).pdf");

        Assert.NotEmpty(await SearchAsync("\"quote\""));
    }

    [Fact]
    public async Task Filters_by_kind_size_and_date()
    {
        await IndexAsync(
            Entry("small.jpg", size: 500_000, modifiedUnix: 1_700_000_000),
            Entry("large.jpg", size: 50_000_000, modifiedUnix: 1_760_000_000),
            Entry("clip.mp4", size: 80_000_000, modifiedUnix: 1_760_000_000),
            Entry("notes.txt", size: 1000, modifiedUnix: 1_760_000_000));

        var images = await _index.SearchAsync(Device, new SearchQuery
        {
            Text = "a",
            Kinds = [MediaKind.Image],
        }, CancellationToken.None);
        Assert.All(images, entry => Assert.Contains(".jpg", entry.Name, StringComparison.Ordinal));

        var big = await _index.SearchAsync(Device, new SearchQuery
        {
            Text = "a",
            MinSize = 10_000_000,
        }, CancellationToken.None);
        Assert.All(big, entry => Assert.True(entry.Size >= 10_000_000));

        var recent = await _index.SearchAsync(Device, new SearchQuery
        {
            Text = "a",
            ModifiedAfter = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000),
        }, CancellationToken.None);
        Assert.DoesNotContain(recent, entry => entry.Name == "small.jpg");
    }

    [Fact]
    public async Task Scopes_a_search_to_a_subtree()
    {
        await IndexAsync(
            Entry("report.pdf", KnownPaths.Documents),
            Entry("report.pdf", KnownPaths.Download));

        var scoped = await _index.SearchAsync(Device, new SearchQuery
        {
            Text = "report",
            Under = KnownPaths.Documents,
        }, CancellationToken.None);

        var found = Assert.Single(scoped);
        Assert.Equal(KnownPaths.Documents, found.Path.Parent);
    }

    [Fact]
    public async Task Two_devices_keep_separate_indexes()
    {
        // Spec §39: the same filename on two phones must not blur together.
        await IndexAsync(Entry("shared-name.jpg"));
        await IndexAsync(Entry("shared-name.jpg", device: OtherDevice));

        var ours = await SearchAsync("shared-name");
        Assert.Single(ours);
        Assert.Equal(Device, ours[0].DeviceId);

        Assert.Equal(1, await _index.CountAsync(Device, CancellationToken.None));
        Assert.Equal(1, await _index.CountAsync(OtherDevice, CancellationToken.None));
    }

    [Fact]
    public async Task Re_indexing_updates_rather_than_duplicates()
    {
        await IndexAsync(Entry("changing.bin", size: 1000));
        await IndexAsync(Entry("changing.bin", size: 9999));

        var results = await SearchAsync("changing");

        var entry = Assert.Single(results);
        Assert.Equal(9999, entry.Size);
    }

    [Fact]
    public async Task Deleted_files_are_pruned_on_the_next_crawl()
    {
        await IndexAsync(Entry("keep.jpg"), Entry("gone.jpg"));

        // The crawl reports what it saw; anything else under the root has been deleted on the device.
        await _index.RemoveMissingAsync(
            Device,
            KnownPaths.InternalStorage,
            new HashSet<string> { KnownPaths.Download.Combine("keep.jpg").Value },
            CancellationToken.None);

        var remaining = await SearchAsync("jpg");
        Assert.DoesNotContain(remaining, entry => entry.Name == "gone.jpg");

        // And the FTS shadow table must have been pruned too, or the name would still match.
        Assert.Empty(await SearchAsync("gone"));
    }

    [Fact]
    public async Task Aggregates_storage_by_category()
    {
        await IndexAsync(
            Entry("a.jpg", size: 3_000_000),
            Entry("b.jpg", size: 2_000_000),
            Entry("c.mp4", size: 50_000_000),
            Entry("d.pdf", size: 500_000));

        var categories = await _index.AggregateByKindAsync(Device, CancellationToken.None);

        var images = categories.Single(category => category.Kind == MediaKind.Image);
        Assert.Equal(2, images.FileCount);
        Assert.Equal(5_000_000, images.Bytes);
        Assert.Equal("Photos", images.Label);

        // Ordered biggest first, so videos lead here.
        Assert.Equal(MediaKind.Video, categories[0].Kind);

        var (bytes, files) = await _index.TotalsAsync(Device, CancellationToken.None);
        Assert.Equal(55_500_000, bytes);
        Assert.Equal(4, files);
    }

    [Fact]
    public async Task Lists_the_largest_files_above_a_threshold()
    {
        await IndexAsync(
            Entry("huge.mp4", size: 2_000_000_000),
            Entry("big.mp4", size: 1_200_000_000),
            Entry("small.jpg", size: 900_000));

        var largest = await _index.LargestFilesAsync(
            Device, 10, 1_000_000_000, CancellationToken.None);

        Assert.Equal(2, largest.Count);
        Assert.Equal("huge.mp4", largest[0].Name);
        Assert.Equal("big.mp4", largest[1].Name);
    }

    [Fact]
    public async Task Breaks_storage_down_by_folder()
    {
        var camera = KnownPaths.Dcim.Combine("Camera");
        var screenshots = KnownPaths.Dcim.Combine("Screenshots");

        await IndexAsync(
            Entry("a.jpg", camera, size: 4_000_000),
            Entry("b.jpg", camera, size: 6_000_000),
            Entry("s.png", screenshots, size: 1_000_000));

        var folders = await _index.FolderBreakdownAsync(Device, KnownPaths.Dcim, CancellationToken.None);

        Assert.Equal(2, folders.Count);
        Assert.Equal("Camera", folders[0].Name);
        Assert.Equal(10_000_000, folders[0].Bytes);
        Assert.Equal(2, folders[0].FileCount);
    }

    [Fact]
    public async Task Groups_files_that_share_a_size()
    {
        await IndexAsync(
            Entry("photo.jpg", size: 5_000_000),
            Entry("photo-copy.jpg", size: 5_000_000),
            Entry("other.jpg", size: 7_000_000),
            Entry("tiny.txt", size: 10));

        var groups = await _index.FindSameSizeGroupsAsync(
            Device, 1024, null, 100, CancellationToken.None);

        // Only the pair sharing a size qualifies; unique sizes cannot be duplicates, and the tiny file is
        // below the minimum.
        var group = Assert.Single(groups);
        Assert.Equal(5_000_000, group.Size);
        Assert.Equal(2, group.Paths.Count);
    }
}
