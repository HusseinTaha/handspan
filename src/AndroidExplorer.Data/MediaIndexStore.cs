using AndroidExplorer.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Data;

/// <summary>
/// The media metadata index behind the gallery (spec §59, §60).
/// </summary>
/// <remarks>
/// The gallery renders from this immediately and rescans in the background, which is what makes opening it
/// feel instant on a phone with tens of thousands of photos.
/// </remarks>
public interface IMediaIndexStore
{
    Task<IReadOnlyList<MediaItem>> QueryAsync(
        DeviceId device,
        MediaKind? kind,
        int skip,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaItem>> QueryFolderAsync(
        DeviceId device,
        DevicePath folder,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(DevicePath Folder, int Count, long Bytes, DateTimeOffset Newest)>> QueryFoldersAsync(
        DeviceId device,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        DeviceId device,
        IReadOnlyList<MediaItem> items,
        CancellationToken cancellationToken);

    Task<int> CountAsync(DeviceId device, CancellationToken cancellationToken);
}

public sealed class SqliteMediaIndexStore(
    IAndroidExplorerDatabase database,
    ILogger<SqliteMediaIndexStore> logger) : IMediaIndexStore
{
    public async Task<IReadOnlyList<MediaItem>> QueryAsync(
        DeviceId device,
        MediaKind? kind,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var filter = kind is null ? string.Empty : " AND Kind = $kind";

        return await ReadAsync(
            device,
            $"""
             SELECT Path, Kind, Size, ModifiedUnix, DateTakenUnix, MimeType, Width, Height, DurationMs
             FROM MediaItems
             WHERE DeviceId = $device{filter}
             ORDER BY COALESCE(DateTakenUnix, ModifiedUnix) DESC
             LIMIT $take OFFSET $skip
             """,
            command =>
            {
                command.Parameters.AddWithValue("$take", take);
                command.Parameters.AddWithValue("$skip", skip);

                if (kind is { } value)
                {
                    command.Parameters.AddWithValue("$kind", (int)value);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaItem>> QueryFolderAsync(
        DeviceId device,
        DevicePath folder,
        CancellationToken cancellationToken)
        => await ReadAsync(
            device,
            """
            SELECT Path, Kind, Size, ModifiedUnix, DateTakenUnix, MimeType, Width, Height, DurationMs
            FROM MediaItems
            WHERE DeviceId = $device AND ParentPath = $folder
            ORDER BY COALESCE(DateTakenUnix, ModifiedUnix) DESC
            """,
            command => command.Parameters.AddWithValue("$folder", folder.Value),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<(DevicePath, int, long, DateTimeOffset)>> QueryFoldersAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        var folders = new List<(DevicePath, int, long, DateTimeOffset)>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ParentPath, COUNT(*), SUM(Size), MAX(COALESCE(DateTakenUnix, ModifiedUnix))
                FROM MediaItems
                WHERE DeviceId = $device
                GROUP BY ParentPath
                ORDER BY 4 DESC
                """;
            command.Parameters.AddWithValue("$device", device.Serial);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DevicePath.TryParse(reader.GetString(0), out var path))
                {
                    folders.Add((
                        path,
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
                }
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not query media folders.");
        }

        return folders;
    }

    public async Task ReplaceAsync(
        DeviceId device,
        IReadOnlyList<MediaItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM MediaItems WHERE DeviceId = $device";
                delete.Parameters.AddWithValue("$device", device.Serial);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO MediaItems
                        (DeviceId, Path, ParentPath, Name, Kind, Size, ModifiedUnix, DateTakenUnix,
                         MimeType, Width, Height, DurationMs)
                    VALUES ($device, $path, $parent, $name, $kind, $size, $modified, $taken,
                            $mime, $width, $height, $duration)
                    ON CONFLICT (DeviceId, Path) DO UPDATE SET
                        Size = $size, ModifiedUnix = $modified, DateTakenUnix = $taken,
                        Width = $width, Height = $height, DurationMs = $duration
                    """;

                var deviceParameter = insert.Parameters.Add("$device", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var parent = insert.Parameters.Add("$parent", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
                var size = insert.Parameters.Add("$size", SqliteType.Integer);
                var modified = insert.Parameters.Add("$modified", SqliteType.Integer);
                var taken = insert.Parameters.Add("$taken", SqliteType.Integer);
                var mime = insert.Parameters.Add("$mime", SqliteType.Text);
                var width = insert.Parameters.Add("$width", SqliteType.Integer);
                var height = insert.Parameters.Add("$height", SqliteType.Integer);
                var duration = insert.Parameters.Add("$duration", SqliteType.Integer);

                deviceParameter.Value = device.Serial;

                foreach (var item in items)
                {
                    path.Value = item.Path.Value;
                    parent.Value = item.Path.Parent.Value;
                    name.Value = item.Name;
                    kind.Value = (int)item.Kind;
                    size.Value = item.Size;
                    modified.Value = item.Modified.ToUnixTimeSeconds();
                    taken.Value = item.DateTaken is { } date
                        ? date.ToUnixTimeSeconds()
                        : (object)DBNull.Value;
                    mime.Value = (object?)item.MimeType ?? DBNull.Value;
                    width.Value = (object?)item.Width ?? DBNull.Value;
                    height.Value = (object?)item.Height ?? DBNull.Value;
                    duration.Value = item.Duration is { } span
                        ? (long)span.TotalMilliseconds
                        : (object)DBNull.Value;

                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not update the media index.");
        }
    }

    public async Task<int> CountAsync(DeviceId device, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM MediaItems WHERE DeviceId = $device";
            command.Parameters.AddWithValue("$device", device.Serial);

            return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private async Task<IReadOnlyList<MediaItem>> ReadAsync(
        DeviceId device,
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaItem>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$device", device.Serial);
            configure(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!DevicePath.TryParse(reader.GetString(0), out var path))
                {
                    continue;
                }

                items.Add(new MediaItem
                {
                    DeviceId = device,
                    Path = path,
                    Kind = (MediaKind)reader.GetInt32(1),
                    Size = reader.GetInt64(2),
                    Modified = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                    DateTaken = reader.IsDBNull(4)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                    MimeType = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Width = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    Height = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Duration = reader.IsDBNull(8)
                        ? null
                        : TimeSpan.FromMilliseconds(reader.GetInt64(8)),
                });
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not query the media index.");
        }

        return items;
    }
}
