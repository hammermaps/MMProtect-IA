using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Infrastructure.Backup;

public interface ISqliteBackupProvider
{
    Task<SqliteBackupResult> CreateSnapshotAsync(string sourceDatabasePath, string snapshotPath, CancellationToken cancellationToken);
}

public sealed record SqliteBackupResult(bool Succeeded, string? ErrorCode);

public sealed class SqliteBackupProvider : ISqliteBackupProvider
{
    public Task<SqliteBackupResult> CreateSnapshotAsync(string sourceDatabasePath, string snapshotPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourceDatabasePath))
        {
            return Task.FromResult(new SqliteBackupResult(false, "SQLITE_DATABASE_NOT_FOUND"));
        }

        try
        {
            SqliteProviderInitializer.Initialize();
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }

            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(snapshotPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return Task.FromResult(new SqliteBackupResult(true, null));
        }
        catch (SqliteException)
        {
            return Task.FromResult(new SqliteBackupResult(false, "SQLITE_BACKUP_FAILED"));
        }
    }
}
