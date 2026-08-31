using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Infrastructure.Persistence;

public interface IAgentDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<string?> GetAgentIdAsync(CancellationToken cancellationToken);

    Task StoreAgentIdAsync(string agentId, CancellationToken cancellationToken);

    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}

public sealed class AgentDatabase : IAgentDatabase
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<AgentDatabase> _logger;

    public AgentDatabase(IOptions<AgentOptions> options, ILogger<AgentDatabase> logger)
    {
        SqliteProviderInitializer.Initialize();
        _logger = logger;
        var dataDirectory = Path.GetFullPath(options.Value.DataDirectory);
        _databasePath = Path.Combine(dataDirectory, "agent.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 15
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        SetUnixModeIfSupported(Path.GetDirectoryName(_databasePath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS agent_metadata (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS instances (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                domain TEXT NOT NULL UNIQUE,
                container_id TEXT NULL,
                container_name TEXT NULL,
                host_port INTEGER NOT NULL UNIQUE,
                database_provider TEXT NOT NULL,
                database_server TEXT NULL,
                database_port INTEGER NULL,
                database_user TEXT NULL,
                database_name TEXT NULL,
                database_ssl_mode TEXT NULL,
                status TEXT NOT NULL,
                data_path TEXT NOT NULL,
                image_reference TEXT NULL,
                image_digest TEXT NULL,
                installed_template_version INTEGER NOT NULL DEFAULT 0,
                config_revision INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS backups (
                id TEXT PRIMARY KEY NOT NULL,
                instance_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                file_path TEXT NOT NULL,
                file_size INTEGER NULL,
                status TEXT NOT NULL,
                database_provider TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                error_code TEXT NULL,
                FOREIGN KEY (instance_id) REFERENCES instances(id)
            );

            CREATE TABLE IF NOT EXISTS ports (
                port INTEGER PRIMARY KEY NOT NULL,
                instance_id TEXT NULL UNIQUE,
                reserved_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY NOT NULL,
                instance_id TEXT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (instance_id) REFERENCES instances(id)
            );

            CREATE TABLE IF NOT EXISTS idempotency_records (
                operation TEXT NOT NULL,
                key TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                response_status INTEGER NOT NULL,
                response_body TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                PRIMARY KEY (operation, key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureInstanceColumnsAsync(connection, cancellationToken);
        await EnsureSoftDeleteFriendlyUniqueIndexesAsync(connection, cancellationToken);
        SetUnixModeIfSupported(_databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _logger.LogInformation("Initialized local agent metadata store");
    }

    public async Task<string?> GetAgentIdAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM agent_metadata WHERE key = 'instance_agent_id';";
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task StoreAgentIdAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_metadata (key, value, updated_at)
            VALUES ('instance_agent_id', $agentId, $updatedAt)
            ON CONFLICT(key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void SetUnixModeIfSupported(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, mode);
    }

    /// <summary>
    /// One-time migration: the original schema declared <c>domain</c> and <c>host_port</c> as
    /// column-level UNIQUE, which permanently reserves both for a soft-deleted (deleteData=false)
    /// instance row and contradicts the documented "port freigeben" behavior on delete. Rebuilds the
    /// table without those inline constraints and replaces them with partial unique indexes that only
    /// apply to non-deleted rows, so a domain/port becomes reusable once its instance is soft-deleted.
    /// Idempotent: skipped once the target indexes already exist.
    /// </summary>
    private static async Task EnsureSoftDeleteFriendlyUniqueIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_instances_domain_active';";
            var alreadyMigrated = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) > 0;
            if (alreadyMigrated)
            {
                return;
            }
        }

        await using (var disableFk = connection.CreateCommand())
        {
            disableFk.CommandText = "PRAGMA foreign_keys = OFF;";
            await disableFk.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.CommandText = """
                BEGIN TRANSACTION;

                CREATE TABLE instances_new (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    domain TEXT NOT NULL,
                    container_id TEXT NULL,
                    container_name TEXT NULL,
                    host_port INTEGER NOT NULL,
                    database_provider TEXT NOT NULL,
                    database_server TEXT NULL,
                    database_port INTEGER NULL,
                    database_user TEXT NULL,
                    database_name TEXT NULL,
                    database_ssl_mode TEXT NULL,
                    status TEXT NOT NULL,
                    data_path TEXT NOT NULL,
                    image_reference TEXT NULL,
                    image_digest TEXT NULL,
                    installed_template_version INTEGER NOT NULL DEFAULT 0,
                    config_revision INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                INSERT INTO instances_new
                    (id, name, domain, container_id, container_name, host_port, database_provider,
                     database_server, database_port, database_user, database_name, database_ssl_mode,
                     status, data_path, image_reference, image_digest, installed_template_version,
                     config_revision, created_at, updated_at)
                SELECT
                    id, name, domain, container_id, container_name, host_port, database_provider,
                    database_server, database_port, database_user, database_name, database_ssl_mode,
                    status, data_path, image_reference, image_digest, installed_template_version,
                    config_revision, created_at, updated_at
                FROM instances;

                DROP TABLE instances;
                ALTER TABLE instances_new RENAME TO instances;

                CREATE UNIQUE INDEX idx_instances_domain_active ON instances(domain) WHERE status != 'deleted';
                CREATE UNIQUE INDEX idx_instances_host_port_active ON instances(host_port) WHERE status != 'deleted';

                COMMIT;
                """;
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var enableFk = connection.CreateCommand())
        {
            enableFk.CommandText = "PRAGMA foreign_keys = ON;";
            await enableFk.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureInstanceColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(instances);";
            await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        await AddColumnIfMissingAsync("database_server", "TEXT NULL");
        await AddColumnIfMissingAsync("database_port", "INTEGER NULL");
        await AddColumnIfMissingAsync("database_user", "TEXT NULL");
        await AddColumnIfMissingAsync("database_name", "TEXT NULL");
        await AddColumnIfMissingAsync("database_ssl_mode", "TEXT NULL");
        await AddColumnIfMissingAsync("data_path", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync("image_reference", "TEXT NULL");
        await AddColumnIfMissingAsync("image_digest", "TEXT NULL");

        async Task AddColumnIfMissingAsync(string column, string definition)
        {
            if (!existingColumns.Add(column))
            {
                return;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE instances ADD COLUMN {column} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
