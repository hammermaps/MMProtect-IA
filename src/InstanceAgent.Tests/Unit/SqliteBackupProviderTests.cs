using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Infrastructure.Backup;
using MmProtect.InstanceAgent.Infrastructure.LicenseServer;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class SqliteBackupProviderTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mmprotect-backup-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateSnapshotAsync_CreatesConsistentSqliteSnapshot()
    {
        var source = Path.Combine(_directory, "source.db");
        var snapshot = Path.Combine(_directory, "snapshot.db");
        await new SqliteLicenseServerDatabaseInitializer().InitializeAsync(source, CancellationToken.None);
        await using (var connection = new SqliteConnection($"Data Source={source}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO customers (customer_uid, external_customer_ref, name) VALUES ('customer-1', 'external-1', 'Customer One');";
            await command.ExecuteNonQueryAsync();
        }

        var result = await new SqliteBackupProvider().CreateSnapshotAsync(source, snapshot, CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var copied = new SqliteConnection($"Data Source={snapshot}");
        await copied.OpenAsync();
        await using var count = copied.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM customers;";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }
}
