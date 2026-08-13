using Handspan.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Handspan.Data;

/// <summary>
/// Persists transfer jobs so an interrupted transfer survives more than a cable pull (spec §13).
/// </summary>
/// <remarks>
/// Without this, killing the application loses the queue and any partial progress. With it, an
/// application crash is just another resumable interruption.
/// </remarks>
public interface ITransferJobStore
{
    Task<IReadOnlyList<TransferJob>> LoadAsync(DeviceId deviceId, CancellationToken cancellationToken);

    Task SaveAsync(TransferJob job, CancellationToken cancellationToken);

    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);

    Task DeleteTerminalAsync(DeviceId deviceId, CancellationToken cancellationToken);
}

public sealed class SqliteTransferJobStore(
    IHandspanDatabase database,
    ILogger<SqliteTransferJobStore> logger) : ITransferJobStore
{
    public async Task<IReadOnlyList<TransferJob>> LoadAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        var jobs = new List<TransferJob>();

        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Direction, RemotePath, LocalPath, TotalBytes, BytesTransferred, Status,
                       ConflictPolicy, Verification, RetryCount, Error, CreatedUnix, StartedUnix,
                       CompletedUnix, BatchId
                FROM TransferJobs
                WHERE DeviceId = $device
                ORDER BY CreatedUnix
                """;
            command.Parameters.AddWithValue("$device", deviceId.Serial);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!DevicePath.TryParse(reader.GetString(2), out var remotePath))
                {
                    continue;
                }

                jobs.Add(new TransferJob
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    DeviceId = deviceId,
                    Direction = (TransferDirection)reader.GetInt32(1),
                    RemotePath = remotePath,
                    LocalPath = reader.GetString(3),
                    TotalBytes = reader.GetInt64(4),
                    BytesTransferred = reader.GetInt64(5),
                    Status = (TransferStatus)reader.GetInt32(6),
                    ConflictPolicy = (ConflictPolicy)reader.GetInt32(7),
                    Verification = (VerificationMode)reader.GetInt32(8),
                    RetryCount = reader.GetInt32(9),
                    Error = reader.IsDBNull(10) ? null : reader.GetString(10),
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)),
                    StartedAt = reader.IsDBNull(12)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)),
                    CompletedAt = reader.IsDBNull(13)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(13)),
                    BatchId = reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
                });
            }
        }
        catch (SqliteException ex)
        {
            // A lost journal costs resume, not correctness.
            logger.LogWarning(ex, "Could not load the transfer journal.");
        }

        return jobs;
    }

    public async Task SaveAsync(TransferJob job, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TransferJobs
                    (Id, DeviceId, Direction, RemotePath, LocalPath, TotalBytes, BytesTransferred,
                     Status, ConflictPolicy, Verification, RetryCount, Error, CreatedUnix, StartedUnix,
                     CompletedUnix, BatchId)
                VALUES ($id, $device, $direction, $remote, $local, $total, $done, $status, $conflict,
                        $verify, $retries, $error, $created, $started, $completed, $batch)
                ON CONFLICT (Id) DO UPDATE SET
                    BytesTransferred = $done,
                    Status = $status,
                    RetryCount = $retries,
                    Error = $error,
                    StartedUnix = $started,
                    CompletedUnix = $completed
                """;

            command.Parameters.AddWithValue("$id", job.Id.ToString());
            command.Parameters.AddWithValue("$device", job.DeviceId.Serial);
            command.Parameters.AddWithValue("$direction", (int)job.Direction);
            command.Parameters.AddWithValue("$remote", job.RemotePath.Value);
            command.Parameters.AddWithValue("$local", job.LocalPath);
            command.Parameters.AddWithValue("$total", job.TotalBytes);
            command.Parameters.AddWithValue("$done", job.BytesTransferred);
            command.Parameters.AddWithValue("$status", (int)job.Status);
            command.Parameters.AddWithValue("$conflict", (int)job.ConflictPolicy);
            command.Parameters.AddWithValue("$verify", (int)job.Verification);
            command.Parameters.AddWithValue("$retries", job.RetryCount);
            command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", job.CreatedAt.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$started",
                job.StartedAt is { } started ? started.ToUnixTimeSeconds() : DBNull.Value);
            command.Parameters.AddWithValue("$completed",
                job.CompletedAt is { } completed ? completed.ToUnixTimeSeconds() : DBNull.Value);
            command.Parameters.AddWithValue("$batch",
                job.BatchId is { } batch ? batch.ToString() : DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not journal a transfer job.");
        }
    }

    public async Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TransferJobs WHERE Id = $id";
            command.Parameters.AddWithValue("$id", jobId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not delete a journalled job.");
        }
    }

    public async Task DeleteTerminalAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM TransferJobs
                WHERE DeviceId = $device AND Status IN ($completed, $failed, $cancelled)
                """;
            command.Parameters.AddWithValue("$device", deviceId.Serial);
            command.Parameters.AddWithValue("$completed", (int)TransferStatus.Completed);
            command.Parameters.AddWithValue("$failed", (int)TransferStatus.Failed);
            command.Parameters.AddWithValue("$cancelled", (int)TransferStatus.Cancelled);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not clear completed jobs.");
        }
    }
}
