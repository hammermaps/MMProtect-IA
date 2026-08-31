using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Infrastructure.LicenseServer;

public interface ISqliteLicenseServerDatabaseInitializer
{
    Task InitializeAsync(string databasePath, CancellationToken cancellationToken);
}

public sealed class SqliteLicenseServerDatabaseInitializer : ISqliteLicenseServerDatabaseInitializer
{
    private const string SchemaResourceName = "MmProtect.InstanceAgent.Infrastructure.LicenseServer.Schemas.license-server-sqlite-v1.sql";

    public async Task InitializeAsync(string databasePath, CancellationToken cancellationToken)
    {
        SqliteProviderInitializer.Initialize();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        await using var schemaStream = typeof(SqliteLicenseServerDatabaseInitializer).Assembly
            .GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException("The versioned SQLite schema resource is missing.");
        using var reader = new StreamReader(schemaStream);
        var schema = await reader.ReadToEndAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
