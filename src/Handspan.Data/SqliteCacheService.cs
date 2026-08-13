using Handspan.Core.Interfaces;
using Handspan.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Handspan.Data;

/// <summary>
/// Caches directory listings so a revisited folder renders immediately (spec §29).
/// </summary>
/// <remarks>
/// The pattern is: show the cached listing, re-read the device in the background, diff, patch. That is
/// what buys the sub-300 ms perceived navigation target (spec §45), and it is necessary because ADB
/// provides no filesystem watcher for shared storage (spec §52).
/// </remarks>
public sealed class SqliteCacheService(
    IHandspanDatabase database,
    ILogger<SqliteCacheService> logger) : ICacheService
{
    public async Task<IReadOnlyList<DeviceEntry>?> GetListingAsync(
        DeviceId deviceId,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var exists = connection.CreateCommand();
            exists.CommandText =
                "SELECT 1 FROM CachedDirectories WHERE DeviceId = $device AND Path = $path";
            exists.Parameters.AddWithValue("$device", deviceId.Serial);
            exists.Parameters.AddWithValue("$path", path.Value);

            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }

            await using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT Name, Kind, Size, IsSizeKnown, ModifiedUnix, Mode, IsSymlink
                FROM CachedEntries
                WHERE DeviceId = $device AND ParentPath = $path
                """;
            query.Parameters.AddWithValue("$device", deviceId.Serial);
            query.Parameters.AddWithValue("$path", path.Value);

            var entries = new List<DeviceEntry>();

            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);

                // A cached name that is no longer representable is dropped rather than crashing a load.
                if (!DevicePath.IsValidFileName(name))
                {
                    continue;
                }

                entries.Add(new DeviceEntry
                {
                    DeviceId = deviceId,
                    Path = path.Combine(name),
                    Kind = (DeviceEntryKind)reader.GetInt32(1),
                    Size = reader.GetInt64(2),
                    IsSizeKnown = reader.GetBoolean(3),
                    Modified = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                    Mode = reader.GetInt32(5),
                    IsSymlink = reader.GetBoolean(6),
                });
            }

            return entries;
        }
        catch (SqliteException ex)
        {
            // A cache miss is always survivable: fall back to reading the device.
            logger.LogDebug(ex, "Could not read a cached listing.");
            return null;
        }
    }

    public async Task SetListingAsync(
        DeviceId deviceId,
        DevicePath path,
        IReadOnlyList<DeviceEntry> entries,
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
                delete.CommandText =
                    "DELETE FROM CachedEntries WHERE DeviceId = $device AND ParentPath = $path";
                delete.Parameters.AddWithValue("$device", deviceId.Serial);
                delete.Parameters.AddWithValue("$path", path.Value);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO CachedEntries
                        (DeviceId, ParentPath, Name, Kind, Size, IsSizeKnown, ModifiedUnix, Mode, IsSymlink)
                    VALUES ($device, $path, $name, $kind, $size, $sizeKnown, $modified, $mode, $symlink)
                    """;

                var device = insert.Parameters.Add("$device", SqliteType.Text);
                var parent = insert.Parameters.Add("$path", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var kind = insert.Parameters.Add("$kind", SqliteType.Integer);
                var size = insert.Parameters.Add("$size", SqliteType.Integer);
                var sizeKnown = insert.Parameters.Add("$sizeKnown", SqliteType.Integer);
                var modified = insert.Parameters.Add("$modified", SqliteType.Integer);
                var mode = insert.Parameters.Add("$mode", SqliteType.Integer);
                var symlink = insert.Parameters.Add("$symlink", SqliteType.Integer);

                device.Value = deviceId.Serial;
                parent.Value = path.Value;

                foreach (var entry in entries)
                {
                    name.Value = entry.Name;
                    kind.Value = (int)entry.Kind;
                    size.Value = entry.Size;
                    sizeKnown.Value = entry.IsSizeKnown;
                    modified.Value = entry.Modified.ToUnixTimeSeconds();
                    mode.Value = entry.Mode;
                    symlink.Value = entry.IsSymlink;

                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (var touch = connection.CreateCommand())
            {
                touch.Transaction = (SqliteTransaction)transaction;
                touch.CommandText = """
                    INSERT INTO CachedDirectories (DeviceId, Path, FetchedUnix)
                    VALUES ($device, $path, $fetched)
                    ON CONFLICT (DeviceId, Path) DO UPDATE SET FetchedUnix = $fetched
                    """;
                touch.Parameters.AddWithValue("$device", deviceId.Serial);
                touch.Parameters.AddWithValue("$path", path.Value);
                touch.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // Failing to cache must never fail the operation the user asked for.
            logger.LogDebug(ex, "Could not store a cached listing.");
        }
    }

    public async Task InvalidateAsync(
        DeviceId deviceId,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM CachedEntries WHERE DeviceId = $device AND ParentPath = $path;
                DELETE FROM CachedDirectories WHERE DeviceId = $device AND Path = $path;
                """;
            command.Parameters.AddWithValue("$device", deviceId.Serial);
            command.Parameters.AddWithValue("$path", path.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not invalidate a cached listing.");
        }
    }

    public async Task InvalidateDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM CachedEntries WHERE DeviceId = $device;
                DELETE FROM CachedDirectories WHERE DeviceId = $device;
                """;
            command.Parameters.AddWithValue("$device", deviceId.Serial);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not clear a device's cache.");
        }
    }
}
