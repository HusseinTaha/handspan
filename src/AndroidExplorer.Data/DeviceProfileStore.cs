using System.Text.Json;
using AndroidExplorer.Core.Interfaces;
using AndroidExplorer.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AndroidExplorer.Data;

/// <summary>
/// Per-device preferences and favourites (spec §65, §67).
/// </summary>
/// <remarks>
/// Kept per device so two phones do not share pinned folders or view preferences — the same reason every
/// other table is keyed by <see cref="DeviceId"/> (spec §39).
/// </remarks>
public interface IDeviceProfileStore
{
    Task<DeviceProfile> GetAsync(DeviceId device, CancellationToken cancellationToken);

    Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken);

    Task<IReadOnlyList<DevicePath>> GetFavoritesAsync(DeviceId device, CancellationToken cancellationToken);

    Task AddFavoriteAsync(DeviceId device, DevicePath path, CancellationToken cancellationToken);

    Task RemoveFavoriteAsync(DeviceId device, DevicePath path, CancellationToken cancellationToken);
}

public sealed class SqliteDeviceProfileStore(
    IAndroidExplorerDatabase database,
    ILogger<SqliteDeviceProfileStore> logger) : IDeviceProfileStore
{
    public async Task<DeviceProfile> GetAsync(DeviceId device, CancellationToken cancellationToken)
    {
        var profile = new DeviceProfile { DeviceId = device };

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DisplayName, LastConnectedUnix, GallerySourcesJson, PreferredView, SortOrder,
                       BenchmarkedConcurrency, LastBackupUnix, LastBackupFolder
                FROM DeviceProfiles WHERE DeviceId = $device
                """;
            command.Parameters.AddWithValue("$device", device.Serial);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                profile = profile with
                {
                    DisplayName = reader.IsDBNull(0) ? null : reader.GetString(0),
                    LastConnected = reader.IsDBNull(1)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                    GallerySources = reader.IsDBNull(2)
                        ? []
                        : ParsePaths(reader.GetString(2)),
                    PreferredView = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SortOrder = reader.IsDBNull(4) ? null : reader.GetString(4),
                    BenchmarkedConcurrency = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    LastBackupAt = reader.IsDBNull(6)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                    LastBackupFolder = reader.IsDBNull(7) ? null : reader.GetString(7),
                };
            }
        }
        catch (Exception ex) when (ex is SqliteException or JsonException)
        {
            logger.LogDebug(ex, "Could not read a device profile.");
        }

        return profile with
        {
            Favorites = await GetFavoritesAsync(device, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DeviceProfiles
                    (DeviceId, DisplayName, LastConnectedUnix, FavoritesJson, GallerySourcesJson,
                     PreferredView, SortOrder, BenchmarkedConcurrency, LastBackupUnix, LastBackupFolder)
                VALUES ($device, $name, $connected, NULL, $gallery, $view, $sort, $concurrency,
                        $backupAt, $backupFolder)
                ON CONFLICT (DeviceId) DO UPDATE SET
                    DisplayName = $name,
                    LastConnectedUnix = $connected,
                    GallerySourcesJson = $gallery,
                    PreferredView = $view,
                    SortOrder = $sort,
                    BenchmarkedConcurrency = $concurrency,
                    LastBackupUnix = $backupAt,
                    LastBackupFolder = $backupFolder
                """;

            command.Parameters.AddWithValue("$device", profile.DeviceId.Serial);
            command.Parameters.AddWithValue("$name", (object?)profile.DisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$connected",
                profile.LastConnected is { } when ? when.ToUnixTimeSeconds() : DBNull.Value);
            command.Parameters.AddWithValue("$gallery",
                profile.GallerySources.Count > 0
                    ? JsonSerializer.Serialize(profile.GallerySources.Select(path => path.Value))
                    : (object)DBNull.Value);
            command.Parameters.AddWithValue("$view", (object?)profile.PreferredView ?? DBNull.Value);
            command.Parameters.AddWithValue("$sort", (object?)profile.SortOrder ?? DBNull.Value);
            command.Parameters.AddWithValue("$concurrency",
                profile.BenchmarkedConcurrency is { } value ? value : (object)DBNull.Value);
            command.Parameters.AddWithValue("$backupAt",
                profile.LastBackupAt is { } backup ? backup.ToUnixTimeSeconds() : DBNull.Value);
            command.Parameters.AddWithValue("$backupFolder",
                (object?)profile.LastBackupFolder ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not save a device profile.");
        }
    }

    public async Task<IReadOnlyList<DevicePath>> GetFavoritesAsync(
        DeviceId device,
        CancellationToken cancellationToken)
    {
        var favorites = new List<DevicePath>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Path FROM Favorites WHERE DeviceId = $device ORDER BY AddedUnix
                """;
            command.Parameters.AddWithValue("$device", device.Serial);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (DevicePath.TryParse(reader.GetString(0), out var path))
                {
                    favorites.Add(path);
                }
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "Could not read favourites.");
        }

        return favorites;
    }

    public async Task AddFavoriteAsync(
        DeviceId device,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Favorites (DeviceId, Path, AddedUnix) VALUES ($device, $path, $added)
                ON CONFLICT (DeviceId, Path) DO NOTHING
                """;
            command.Parameters.AddWithValue("$device", device.Serial);
            command.Parameters.AddWithValue("$path", path.Value);
            command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not add a favourite.");
        }
    }

    public async Task RemoveFavoriteAsync(
        DeviceId device,
        DevicePath path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Favorites WHERE DeviceId = $device AND Path = $path";
            command.Parameters.AddWithValue("$device", device.Serial);
            command.Parameters.AddWithValue("$path", path.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not remove a favourite.");
        }
    }

    private static IReadOnlyList<DevicePath> ParsePaths(string json)
    {
        var values = JsonSerializer.Deserialize<List<string>>(json) ?? [];

        return values
            .Select(value => DevicePath.TryParse(value, out var path) ? path : (DevicePath?)null)
            .Where(path => path is not null)
            .Select(path => path!.Value)
            .ToList();
    }
}
