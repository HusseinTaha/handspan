using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Handspan.Data;

/// <summary>
/// The searchable file index (spec §28).
/// </summary>
/// <remarks>
/// Queries hit this, never a recursive device scan — that is the whole point of having an index. Writes go
/// through <see cref="UpsertBatchAsync"/> in transactions of a few thousand rows so a 50,000-file crawl does
/// not spend its life in SQLite overhead.
/// </remarks>
public interface IFileIndexStore
{
    Task UpsertBatchAsync(
        DeviceId device,
        IReadOnlyList<DeviceEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Removes rows for paths no longer present under a scanned root.</summary>
    Task RemoveMissingAsync(
        DeviceId device,
        DevicePath root,
        IReadOnlySet<string> presentPaths,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceEntry>> SearchAsync(
        DeviceId device,
        SearchQuery query,
        CancellationToken cancellationToken);

    Task<int> CountAsync(DeviceId device, CancellationToken cancellationToken);

    Task<DateTimeOffset?> GetLastIndexedAsync(DeviceId device, CancellationToken cancellationToken);

    Task MarkIndexedAsync(DeviceId device, CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageCategory>> AggregateByKindAsync(
        DeviceId device,
        CancellationToken cancellationToken);

    Task<(long Bytes, int Files)> TotalsAsync(DeviceId device, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceEntry>> LargestFilesAsync(
        DeviceId device,
        int count,
        long minimumBytes,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StorageFolder>> FolderBreakdownAsync(
        DeviceId device,
        DevicePath parent,
        CancellationToken cancellationToken);

    /// <summary>Files sharing a size, the cheap first pass of duplicate detection (spec §61).</summary>
    Task<IReadOnlyList<(long Size, IReadOnlyList<DevicePath> Paths)>> FindSameSizeGroupsAsync(
        DeviceId device,
        long minimumBytes,
        DevicePath? under,
        int maxGroups,
        CancellationToken cancellationToken);
}

public sealed class SqliteFileIndexStore(
    IHandspanDatabase database,
    ILogger<SqliteFileIndexStore> logger) : IFileIndexStore
{
    public async Task UpsertBatchAsync(
        DeviceId device,
        IReadOnlyList<DeviceEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO FileIndex
                    (DeviceId, Path, ParentPath, Name, Extension, Size, ModifiedUnix, IsDirectory,
                     MediaKind, Mode)
                VALUES ($device, $path, $parent, $name, $extension, $size, $modified, $isDirectory,
                        $kind, $mode)
                ON CONFLICT (DeviceId, Path) DO UPDATE SET
                    Size = $size, ModifiedUnix = $modified, MediaKind = $kind, Mode = $mode
                """;

            var deviceParameter = command.Parameters.Add("$device", SqliteType.Text);
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var parent = command.Parameters.Add("$parent", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var extension = command.Parameters.Add("$extension", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var modified = command.Parameters.Add("$modified", SqliteType.Integer);
            var isDirectory = command.Parameters.Add("$isDirectory", SqliteType.Integer);
            var kind = command.Parameters.Add("$kind", SqliteType.Integer);
            var mode = command.Parameters.Add("$mode", SqliteType.Integer);

            deviceParameter.Value = device.Serial;

            foreach (var entry in entries)
            {
                path.Value = entry.Path.Value;
                parent.Value = entry.Path.Parent.Value;
                name.Value = entry.Name;
                extension.Value = entry.Extension.Length > 0 ? entry.Extension : (object)DBNull.Value;
                size.Value = entry.Size;
                modified.Value = entry.Modified.ToUnixTimeSeconds();
                isDirectory.Value = entry.IsDirectory;
                kind.Value = (int)MediaTypes.FromPath(entry.Path);
                mode.Value = entry.Mode;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not write a batch to the file index.");
        }
    }

    public async Task RemoveMissingAsync(
        DeviceId device,
        DevicePath root,
        IReadOnlySet<string> presentPaths,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Read the indexed paths under this root, then delete the ones the crawl did not see. Doing the
            // diff in memory keeps the SQL simple and the crawl is already holding the path set.
            var stale = new List<string>();

            await using (var query = connection.CreateCommand())
            {
                query.CommandText = """
                    SELECT Path FROM FileIndex
                    WHERE DeviceId = $device AND (Path = $root OR Path LIKE $prefix)
                    """;
                query.Parameters.AddWithValue("$device", device.Serial);
                query.Parameters.AddWithValue("$root", root.Value);
                query.Parameters.AddWithValue("$prefix", root.Value + "/%");

                await using var reader = await query.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var indexed = reader.GetString(0);
                    if (!presentPaths.Contains(indexed))
                    {
                        stale.Add(indexed);
                    }
                }
            }

            if (stale.Count == 0)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM FileIndex WHERE DeviceId = $device AND Path = $path";
                delete.Parameters.AddWithValue("$device", device.Serial);
                var pathParameter = delete.Parameters.Add("$path", SqliteType.Text);

                foreach (var path in stale)
                {
                    pathParameter.Value = path;
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Removed {Count} stale entries from the index.", stale.Count);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not prune the file index.");
        }
    }

    /// <summary>
    /// Searches names, combining a full-text match with a substring fallback.
    /// </summary>
    /// <remarks>
    /// FTS tokenizes on separators, so "invoice" finds <c>old-invoice.jpg</c> and prefix matching finds
    /// <c>invoice-2026.pdf</c> — but it cannot match mid-token, so "voice" would miss "invoice". The LIKE
    /// pass covers that. Both are unioned and ranked so a token match sorts above a substring match.
    /// </remarks>
    public async Task<IReadOnlyList<DeviceEntry>> SearchAsync(
        DeviceId device,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        var text = query.Text.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var results = new List<DeviceEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

            var filters = BuildFilters(query, out var filterSql);

            // Pass 1: full-text, prefix-matched on the final term.
            var ftsQuery = BuildMatchExpression(text);
            if (ftsQuery is not null)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT f.Path, f.Name, f.Size, f.ModifiedUnix, f.IsDirectory, f.Mode
                    FROM FileIndexFts fts
                    JOIN FileIndex f ON f.Id = fts.rowid
                    WHERE fts.Name MATCH $match AND f.DeviceId = $device{filterSql}
                    ORDER BY f.ModifiedUnix DESC
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$match", ftsQuery);
                command.Parameters.AddWithValue("$device", device.Serial);
                command.Parameters.AddWithValue("$limit", query.Limit);
                foreach (var (name, value) in filters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                await ReadInto(command, device, results, seen, cancellationToken).ConfigureAwait(false);
            }

            // Pass 2: substring fallback for what FTS cannot reach.
            if (results.Count < query.Limit)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT Path, Name, Size, ModifiedUnix, IsDirectory, Mode
                    FROM FileIndex
                    WHERE DeviceId = $device AND Name LIKE $like{filterSql}
                    ORDER BY ModifiedUnix DESC
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$like", $"%{text}%");
                command.Parameters.AddWithValue("$device", device.Serial);
                command.Parameters.AddWithValue("$limit", query.Limit);
                foreach (var (name, value) in filters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                await ReadInto(command, device, results, seen, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Search failed.");
        }

        return results.Take(query.Limit).ToList();
    }

    private static List<(string Name, object Value)> BuildFilters(SearchQuery query, out string sql)
    {
        var parameters = new List<(string, object)>();
        var clauses = new List<string>();
        var column = string.Empty;

        // The FTS pass aliases the table, the LIKE pass does not; unqualified column names work for both
        // because the joined query only has one candidate for each of these.
        if (!query.IncludeDirectories)
        {
            clauses.Add($" AND {column}IsDirectory = 0");
        }

        if (query.Kinds.Count > 0)
        {
            var kinds = string.Join(',', query.Kinds.Select(kind => (int)kind));
            clauses.Add($" AND {column}MediaKind IN ({kinds})");
        }

        if (query.MinSize is { } minimum)
        {
            clauses.Add($" AND {column}Size >= $minSize");
            parameters.Add(("$minSize", minimum));
        }

        if (query.MaxSize is { } maximum)
        {
            clauses.Add($" AND {column}Size <= $maxSize");
            parameters.Add(("$maxSize", maximum));
        }

        if (query.ModifiedAfter is { } after)
        {
            clauses.Add($" AND {column}ModifiedUnix >= $after");
            parameters.Add(("$after", after.ToUnixTimeSeconds()));
        }

        if (query.ModifiedBefore is { } before)
        {
            clauses.Add($" AND {column}ModifiedUnix <= $before");
            parameters.Add(("$before", before.ToUnixTimeSeconds()));
        }

        if (query.Under is { } under)
        {
            clauses.Add($" AND ({column}Path = $under OR {column}Path LIKE $underPrefix)");
            parameters.Add(("$under", under.Value));
            parameters.Add(("$underPrefix", under.Value + "/%"));
        }

        sql = string.Concat(clauses);
        return parameters;
    }

    /// <summary>
    /// Builds an FTS5 MATCH expression, quoting each term so punctuation cannot be read as syntax.
    /// </summary>
    private static string? BuildMatchExpression(string text)
    {
        var terms = text
            .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Replace("\"", string.Empty, StringComparison.Ordinal))
            .Where(term => term.Length > 0)
            .ToList();

        if (terms.Count == 0)
        {
            return null;
        }

        // Prefix-match the last term so results appear while the user is still typing.
        var quoted = terms.Select((term, index) =>
            index == terms.Count - 1 ? $"\"{term}\"*" : $"\"{term}\"");

        return string.Join(' ', quoted);
    }

    private static async Task ReadInto(
        SqliteCommand command,
        DeviceId device,
        List<DeviceEntry> results,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var raw = reader.GetString(0);
            if (!seen.Add(raw) || !DevicePath.TryParse(raw, out var path))
            {
                continue;
            }

            results.Add(new DeviceEntry
            {
                DeviceId = device,
                Path = path,
                Kind = reader.GetBoolean(4) ? DeviceEntryKind.Directory : DeviceEntryKind.File,
                Size = reader.GetInt64(2),
                Modified = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                Mode = reader.GetInt32(5),
            });
        }
    }

    public async Task<int> CountAsync(DeviceId device, CancellationToken cancellationToken)
        => await ScalarAsync(
            "SELECT COUNT(*) FROM FileIndex WHERE DeviceId = $device",
            device, cancellationToken).ConfigureAwait(false) is { } value
            ? Convert.ToInt32(value)
            : 0;

    public async Task<DateTimeOffset?> GetLastIndexedAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(
            "SELECT CompletedUnix FROM IndexRuns WHERE DeviceId = $device",
            device, cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value));
    }

    public async Task MarkIndexedAsync(DeviceId device, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO IndexRuns (DeviceId, CompletedUnix) VALUES ($device, $completed)
                ON CONFLICT (DeviceId) DO UPDATE SET CompletedUnix = $completed
                """;
            command.Parameters.AddWithValue("$device", device.Serial);
            command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not record the index run.");
        }
    }

    public async Task<IReadOnlyList<StorageCategory>> AggregateByKindAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        var categories = new List<StorageCategory>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT MediaKind, COUNT(*), SUM(Size)
                FROM FileIndex
                WHERE DeviceId = $device AND IsDirectory = 0
                GROUP BY MediaKind
                ORDER BY 3 DESC
                """;
            command.Parameters.AddWithValue("$device", device.Serial);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var kind = (MediaKind)reader.GetInt32(0);
                categories.Add(new StorageCategory(
                    kind,
                    kind switch
                    {
                        MediaKind.Image => "Photos",
                        MediaKind.Video => "Videos",
                        MediaKind.Audio => "Audio",
                        MediaKind.Document => "Documents",
                        _ => "Other files",
                    },
                    reader.GetInt32(1),
                    reader.GetInt64(2)));
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not aggregate storage by kind.");
        }

        return categories;
    }

    public async Task<(long Bytes, int Files)> TotalsAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(Size), 0), COUNT(*)
                FROM FileIndex WHERE DeviceId = $device AND IsDirectory = 0
                """;
            command.Parameters.AddWithValue("$device", device.Serial);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? (reader.GetInt64(0), reader.GetInt32(1))
                : (0, 0);
        }
        catch (SqliteException)
        {
            return (0, 0);
        }
    }

    public async Task<IReadOnlyList<DeviceEntry>> LargestFilesAsync(
        DeviceId device,
        int count,
        long minimumBytes,
        CancellationToken cancellationToken)
    {
        var results = new List<DeviceEntry>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Path, Name, Size, ModifiedUnix, IsDirectory, Mode
                FROM FileIndex
                WHERE DeviceId = $device AND IsDirectory = 0 AND Size >= $minimum
                ORDER BY Size DESC
                LIMIT $count
                """;
            command.Parameters.AddWithValue("$device", device.Serial);
            command.Parameters.AddWithValue("$minimum", minimumBytes);
            command.Parameters.AddWithValue("$count", count);

            await ReadInto(command, device, results, [], cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not query largest files.");
        }

        return results;
    }

    public async Task<IReadOnlyList<StorageFolder>> FolderBreakdownAsync(
        DeviceId device,
        DevicePath parent,
        CancellationToken cancellationToken)
    {
        var folders = new List<StorageFolder>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            // Group by the path segment immediately below the parent, summing recursively beneath it.
            command.CommandText = """
                SELECT
                    $parent || '/' || SUBSTR(
                        SUBSTR(Path, LENGTH($parent) + 2),
                        1,
                        CASE INSTR(SUBSTR(Path, LENGTH($parent) + 2), '/')
                            WHEN 0 THEN LENGTH(SUBSTR(Path, LENGTH($parent) + 2))
                            ELSE INSTR(SUBSTR(Path, LENGTH($parent) + 2), '/') - 1
                        END) AS Child,
                    COUNT(*),
                    SUM(Size)
                FROM FileIndex
                WHERE DeviceId = $device AND IsDirectory = 0 AND Path LIKE $prefix
                GROUP BY Child
                ORDER BY 3 DESC
                """;
            command.Parameters.AddWithValue("$device", device.Serial);
            command.Parameters.AddWithValue("$parent", parent.Value);
            command.Parameters.AddWithValue("$prefix", parent.Value + "/%");

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DevicePath.TryParse(reader.GetString(0), out var path))
                {
                    folders.Add(new StorageFolder(path, path.Name, reader.GetInt32(1), reader.GetInt64(2)));
                }
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not compute a folder breakdown.");
        }

        return folders;
    }

    public async Task<IReadOnlyList<(long Size, IReadOnlyList<DevicePath> Paths)>> FindSameSizeGroupsAsync(
        DeviceId device,
        long minimumBytes,
        DevicePath? under,
        int maxGroups,
        CancellationToken cancellationToken)
    {
        var groups = new List<(long, IReadOnlyList<DevicePath>)>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

            var scope = under is { } root ? " AND (Path = $under OR Path LIKE $underPrefix)" : string.Empty;

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT Size, GROUP_CONCAT(Path, CHAR(10))
                FROM FileIndex
                WHERE DeviceId = $device AND IsDirectory = 0 AND Size >= $minimum{scope}
                GROUP BY Size
                HAVING COUNT(*) > 1
                ORDER BY Size DESC
                LIMIT $maxGroups
                """;
            command.Parameters.AddWithValue("$device", device.Serial);
            command.Parameters.AddWithValue("$minimum", minimumBytes);
            command.Parameters.AddWithValue("$maxGroups", maxGroups);

            if (under is { } scoped)
            {
                command.Parameters.AddWithValue("$under", scoped.Value);
                command.Parameters.AddWithValue("$underPrefix", scoped.Value + "/%");
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var size = reader.GetInt64(0);

                // Newline-joined because it is the one character an Android filename cannot contain
                // alongside '/', making it a safe separator here.
                var paths = reader.GetString(1)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => DevicePath.TryParse(value, out var path) ? path : (DevicePath?)null)
                    .Where(path => path is not null)
                    .Select(path => path!.Value)
                    .ToList();

                if (paths.Count > 1)
                {
                    groups.Add((size, paths));
                }
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not group files by size.");
        }

        return groups;
    }

    private async Task<object?> ScalarAsync(
        string sql,
        DeviceId device,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$device", device.Serial);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
