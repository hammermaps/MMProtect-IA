using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Infrastructure.LicenseServer;
using Xunit;

namespace MmProtect.InstanceAgent.Tests.Unit;

public sealed class SqliteLicenseServerDatabaseInitializerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mmprotect-schema-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializeAsync_CreatesVersionedLicenseServerSchema()
    {
        var databasePath = Path.Combine(_directory, "mm_license.db");
        var initializer = new SqliteLicenseServerDatabaseInitializer();

        await initializer.InitializeAsync(databasePath, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('customers', 'builds', 'telemetry_events');";
        Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }
}
