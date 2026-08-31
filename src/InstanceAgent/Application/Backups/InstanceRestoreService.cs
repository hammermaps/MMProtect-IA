using System.IO.Compression;
using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Options;
using MmProtect.InstanceAgent.Infrastructure.System;
using Microsoft.Extensions.Options;

namespace MmProtect.InstanceAgent.Application.Backups;

public interface IInstanceRestoreService
{
    Task<InstanceRestoreResult> RestoreAsync(string instanceId, Stream archiveStream, CancellationToken cancellationToken);
}

public sealed record InstanceRestoreResult(bool Succeeded, string? ErrorCode, string? Status);

/// <summary>Restores a validated SQLite archive by swapping the complete instance data directory on one filesystem.</summary>
public sealed class InstanceRestoreService(
    IInstanceRepository instances,
    IRestoreArchiveValidator archiveValidator,
    IInstanceBackupService backups,
    IInstanceLifecycleService lifecycle,
    IInstanceHealthProbe healthProbe,
    IOptions<AgentOptions> options,
    ILogger<InstanceRestoreService> logger) : IInstanceRestoreService
{
    public async Task<InstanceRestoreResult> RestoreAsync(string instanceId, Stream archiveStream, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken);
        if (instance is null) return new(false, "INSTANCE_NOT_FOUND", null);
        if (!string.Equals(instance.DatabaseProvider, "sqlite", StringComparison.OrdinalIgnoreCase)) return new(false, "RESTORE_DATABASE_UNSUPPORTED", null);

        var root = Path.GetFullPath(options.Value.DataDirectory);
        var temporaryDirectory = Path.Combine(root, "tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var archivePath = Path.Combine(temporaryDirectory, $"restore-{Guid.NewGuid():N}.zip");
        var stagingPath = $"{instance.DataPath}.restore-{Guid.NewGuid():N}";
        var rollbackPath = $"{instance.DataPath}.rollback-{Guid.NewGuid():N}";
        var previousMoved = false;
        var replacementMoved = false;
        var wasRunning = string.Equals(instance.Status, "running", StringComparison.OrdinalIgnoreCase);

        try
        {
            await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await CopyArchiveAsync(archiveStream, destination, options.Value.RestoreMaxArchiveBytes, cancellationToken);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(archivePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            await using (var validationStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            {
                var validation = await archiveValidator.ValidateAsync(validationStream, instanceId, cancellationToken);
                if (!validation.Succeeded) return new(false, validation.ErrorCode, null);
            }

            var preRestoreBackup = await backups.CreateAsync(instanceId, cancellationToken);
            if (!preRestoreBackup.Succeeded) return new(false, "RESTORE_PRE_BACKUP_FAILED", null);

            if (wasRunning)
            {
                var stop = await lifecycle.ExecuteAsync(instanceId, InstanceLifecycleOperation.Stop, cancellationToken);
                if (!stop.Succeeded) return new(false, stop.ErrorCode, null);
            }

            await ExtractStagingAsync(archivePath, stagingPath, options.Value.RestoreMaxExtractedBytes, cancellationToken);
            if (!await IsValidSqliteDatabaseAsync(Path.Combine(stagingPath, "mm_license.db"), cancellationToken))
                throw new RestoreFailureException("RESTORE_DATABASE_INVALID");

            Directory.Move(instance.DataPath, rollbackPath);
            previousMoved = true;
            Directory.Move(stagingPath, instance.DataPath);
            replacementMoved = true;

            if (wasRunning)
            {
                var start = await lifecycle.ExecuteAsync(instanceId, InstanceLifecycleOperation.Start, cancellationToken);
                if (!start.Succeeded) throw new RestoreStartException(start.ErrorCode!);
                if (!await healthProbe.IsHealthyAsync(instance.HostPort, cancellationToken)) throw new RestoreFailureException("INSTANCE_HEALTHCHECK_FAILED");
            }

            if (Directory.Exists(rollbackPath)) Directory.Delete(rollbackPath, recursive: true);
            logger.LogInformation("SQLite restore completed for {InstanceId}; pre-restore backup {BackupId}", instanceId, preRestoreBackup.BackupId);
            return new(true, null, wasRunning ? "running" : "stopped");
        }
        catch (RestoreStartException exception)
        {
            await RollbackAsync(instanceId, instance.DataPath, rollbackPath, previousMoved, replacementMoved, wasRunning, cancellationToken);
            return new(false, exception.ErrorCode, null);
        }
        catch (RestoreFailureException exception)
        {
            await RollbackAsync(instanceId, instance.DataPath, rollbackPath, previousMoved, replacementMoved, wasRunning, cancellationToken);
            return new(false, exception.ErrorCode, null);
        }
        catch (RestoreArchiveTooLargeException)
        {
            return new(false, "RESTORE_ARCHIVE_TOO_LARGE", null);
        }
        catch (IOException)
        {
            await RollbackAsync(instanceId, instance.DataPath, rollbackPath, previousMoved, replacementMoved, wasRunning, cancellationToken);
            return new(false, "RESTORE_IO_FAILED", null);
        }
        catch (InvalidDataException)
        {
            return new(false, "RESTORE_ARCHIVE_INVALID", null);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        }
    }

    private async Task RollbackAsync(string instanceId, string dataPath, string rollbackPath, bool previousMoved, bool replacementMoved, bool wasRunning, CancellationToken cancellationToken)
    {
        try
        {
            if (replacementMoved && Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
            if (previousMoved && Directory.Exists(rollbackPath)) Directory.Move(rollbackPath, dataPath);
            if (wasRunning) await lifecycle.ExecuteAsync(instanceId, InstanceLifecycleOperation.Start, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Restore rollback failed for {InstanceId}", instanceId);
        }
    }

    private static async Task CopyArchiveAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            if (read > maximumBytes - copied) throw new RestoreArchiveTooLargeException();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }
    }

    private static async Task ExtractStagingAsync(string archivePath, string stagingPath, long maximumBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingPath);
        using var archive = ZipFile.OpenRead(archivePath);
        long extracted = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName == "database/mm_license.db")
            {
                if (entry.Length > maximumBytes - extracted) throw new RestoreArchiveTooLargeException();
                await ExtractFileAsync(entry, Path.Combine(stagingPath, "mm_license.db"), cancellationToken);
                extracted += entry.Length;
                continue;
            }
            if (!entry.FullName.StartsWith("data/", StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;

            var relative = entry.FullName["data/".Length..];
            if (string.Equals(relative, "mm_license.db", StringComparison.Ordinal)) continue;
            if (!IsSafeRelativePath(relative)) throw new InvalidDataException("Unsafe ZIP entry path.");
            var target = Path.GetFullPath(Path.Combine(stagingPath, relative));
            if (!target.StartsWith(Path.GetFullPath(stagingPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe ZIP entry path.");
            if (entry.Length > maximumBytes - extracted) throw new RestoreArchiveTooLargeException();
            await ExtractFileAsync(entry, target, cancellationToken);
            extracted += entry.Length;
        }
    }

    private static bool IsSafeRelativePath(string path) =>
        !path.Contains('\\') && !Path.IsPathRooted(path) && path.Split('/').All(segment => segment is not "" and not "." and not "..");

    private static async Task ExtractFileAsync(ZipArchiveEntry entry, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = entry.Open();
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<bool> IsValidSqliteDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException) { return false; }
    }

    private sealed class RestoreStartException(string errorCode) : Exception
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed class RestoreFailureException(string errorCode) : Exception
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed class RestoreArchiveTooLargeException : Exception;
}
