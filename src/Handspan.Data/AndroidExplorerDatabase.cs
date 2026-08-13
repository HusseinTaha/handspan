using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Handspan.Data;

/// <summary>Owns the SQLite database and its schema.</summary>
public interface IHandspanDatabase
{
    /// <summary>Opens a connection with the schema guaranteed to be present.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The local store behind the directory cache (phase 2), the transfer journal (phase 3) and the file
/// and media index (phase 5).
/// </summary>
/// <remarks>
/// One file in the application data folder, so deleting it resets all caches and nothing else. Every
/// table is keyed by device (spec §39): two connected phones must never share a row.
/// </remarks>
public sealed class HandspanDatabase(string databasePath, ILogger<HandspanDatabase> logger)
    : IHandspanDatabase
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    }.ToString();

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);

            foreach (var migration in Migrations)
            {
                try
                {
                    await ExecuteAsync(connection, migration, cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column",
                                                    StringComparison.OrdinalIgnoreCase))
                {
                    // Already applied on a database from an earlier run.
                }
            }

            _initialized = true;
            logger.LogInformation("Local cache database is ready.");
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The schema. WAL keeps background refreshes from blocking reads, which is what lets a cached
    /// listing render while the device is being re-read (spec §29).
    /// </summary>
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;

        -- Directory listing cache (spec section 29).
        CREATE TABLE IF NOT EXISTS CachedDirectories (
            DeviceId    TEXT    NOT NULL,
            Path        TEXT    NOT NULL,
            FetchedUnix INTEGER NOT NULL,
            PRIMARY KEY (DeviceId, Path)
        );

        CREATE TABLE IF NOT EXISTS CachedEntries (
            DeviceId     TEXT    NOT NULL,
            ParentPath   TEXT    NOT NULL,
            Name         TEXT    NOT NULL,
            Kind         INTEGER NOT NULL,
            Size         INTEGER NOT NULL,
            IsSizeKnown  INTEGER NOT NULL,
            ModifiedUnix INTEGER NOT NULL,
            Mode         INTEGER NOT NULL,
            IsSymlink    INTEGER NOT NULL,
            PRIMARY KEY (DeviceId, ParentPath, Name)
        );

        CREATE INDEX IF NOT EXISTS IX_CachedEntries_Parent
            ON CachedEntries (DeviceId, ParentPath);

        -- Transfer journal (spec section 13): resume must survive an application crash.
        CREATE TABLE IF NOT EXISTS TransferJobs (
            Id               TEXT    NOT NULL PRIMARY KEY,
            DeviceId         TEXT    NOT NULL,
            Direction        INTEGER NOT NULL,
            RemotePath       TEXT    NOT NULL,
            LocalPath        TEXT    NOT NULL,
            TotalBytes       INTEGER NOT NULL,
            BytesTransferred INTEGER NOT NULL,
            Status           INTEGER NOT NULL,
            ConflictPolicy   INTEGER NOT NULL,
            Verification     INTEGER NOT NULL,
            RetryCount       INTEGER NOT NULL,
            Error            TEXT    NULL,
            CreatedUnix      INTEGER NOT NULL,
            StartedUnix      INTEGER NULL,
            CompletedUnix    INTEGER NULL,
            BatchId          TEXT    NULL
        );

        CREATE INDEX IF NOT EXISTS IX_TransferJobs_Device_Status
            ON TransferJobs (DeviceId, Status);

        -- Searchable file index (spec section 28).
        CREATE TABLE IF NOT EXISTS FileIndex (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            DeviceId     TEXT    NOT NULL,
            Path         TEXT    NOT NULL,
            ParentPath   TEXT    NOT NULL,
            Name         TEXT    NOT NULL,
            Extension    TEXT    NULL,
            Size         INTEGER NOT NULL,
            ModifiedUnix INTEGER NOT NULL,
            IsDirectory  INTEGER NOT NULL,
            MediaKind    INTEGER NOT NULL,
            Mode         INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX IF NOT EXISTS IX_FileIndex_Device_Path ON FileIndex (DeviceId, Path);
        CREATE INDEX IF NOT EXISTS IX_FileIndex_Device_Size ON FileIndex (DeviceId, Size DESC);
        CREATE INDEX IF NOT EXISTS IX_FileIndex_Device_Modified ON FileIndex (DeviceId, ModifiedUnix DESC);
        CREATE INDEX IF NOT EXISTS IX_FileIndex_Device_Parent ON FileIndex (DeviceId, ParentPath);
        CREATE INDEX IF NOT EXISTS IX_FileIndex_Device_Kind ON FileIndex (DeviceId, MediaKind);

        -- Full-text filename search. remove_diacritics matters: without it Arabic, CJK and accented
        -- filenames match poorly, and this app exists partly to handle those well (spec section 74).
        CREATE VIRTUAL TABLE IF NOT EXISTS FileIndexFts USING fts5 (
            Name,
            content='FileIndex',
            content_rowid='Id',
            tokenize="unicode61 remove_diacritics 2"
        );

        CREATE TRIGGER IF NOT EXISTS FileIndex_AfterInsert AFTER INSERT ON FileIndex BEGIN
            INSERT INTO FileIndexFts (rowid, Name) VALUES (new.Id, new.Name);
        END;

        CREATE TRIGGER IF NOT EXISTS FileIndex_AfterDelete AFTER DELETE ON FileIndex BEGIN
            INSERT INTO FileIndexFts (FileIndexFts, rowid, Name) VALUES ('delete', old.Id, old.Name);
        END;

        CREATE TRIGGER IF NOT EXISTS FileIndex_AfterUpdate AFTER UPDATE ON FileIndex BEGIN
            INSERT INTO FileIndexFts (FileIndexFts, rowid, Name) VALUES ('delete', old.Id, old.Name);
            INSERT INTO FileIndexFts (rowid, Name) VALUES (new.Id, new.Name);
        END;

        CREATE TABLE IF NOT EXISTS IndexRuns (
            DeviceId      TEXT    NOT NULL PRIMARY KEY,
            CompletedUnix INTEGER NOT NULL
        );

        -- Favourites and quick access (spec section 65).
        CREATE TABLE IF NOT EXISTS Favorites (
            DeviceId  TEXT    NOT NULL,
            Path      TEXT    NOT NULL,
            AddedUnix INTEGER NOT NULL,
            PRIMARY KEY (DeviceId, Path)
        );

        -- Media index behind the gallery (spec section 59).
        CREATE TABLE IF NOT EXISTS MediaItems (
            DeviceId      TEXT    NOT NULL,
            Path          TEXT    NOT NULL,
            ParentPath    TEXT    NOT NULL,
            Name          TEXT    NOT NULL,
            Kind          INTEGER NOT NULL,
            Size          INTEGER NOT NULL,
            ModifiedUnix  INTEGER NOT NULL,
            DateTakenUnix INTEGER NULL,
            MimeType      TEXT    NULL,
            Width         INTEGER NULL,
            Height        INTEGER NULL,
            DurationMs    INTEGER NULL,
            PRIMARY KEY (DeviceId, Path)
        );

        -- The timeline orders by capture date, falling back to file time (spec section 25).
        CREATE INDEX IF NOT EXISTS IX_MediaItems_Timeline
            ON MediaItems (DeviceId, COALESCE(DateTakenUnix, ModifiedUnix) DESC);

        CREATE INDEX IF NOT EXISTS IX_MediaItems_Parent
            ON MediaItems (DeviceId, ParentPath);

        -- Per-device preferences (spec section 67).
        CREATE TABLE IF NOT EXISTS DeviceProfiles (
            DeviceId              TEXT NOT NULL PRIMARY KEY,
            DisplayName           TEXT NULL,
            LastConnectedUnix     INTEGER NULL,
            FavoritesJson         TEXT NULL,
            GallerySourcesJson    TEXT NULL,
            PreferredView         TEXT NULL,
            SortOrder             TEXT NULL,
            BenchmarkedConcurrency INTEGER NULL
        );
        """;

    /// <summary>
    /// Columns added after the first release.
    /// </summary>
    /// <remarks>
    /// Applied separately and tolerantly: SQLite has no "ADD COLUMN IF NOT EXISTS", and an existing database
    /// must keep working rather than being wiped. A duplicate-column error simply means it is already there.
    /// </remarks>
    private static readonly string[] Migrations =
    [
        "ALTER TABLE DeviceProfiles ADD COLUMN LastBackupUnix INTEGER NULL",
        "ALTER TABLE DeviceProfiles ADD COLUMN LastBackupFolder TEXT NULL",
    ];
}
