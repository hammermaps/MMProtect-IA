using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Application.Instances;

public sealed record ManagedInstance(
    string Id,
    string Name,
    string Domain,
    string? ContainerId,
    string? ContainerName,
    int HostPort,
    string DatabaseProvider,
    string? DatabaseServer,
    int? DatabasePort,
    string? DatabaseUser,
    string? DatabaseName,
    string? DatabaseSslMode,
    string Status,
    string DataPath,
    string? ImageReference,
    string? ImageDigest,
    int InstalledTemplateVersion,
    int ConfigRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum CreateInstanceResult
{
    Created,
    DomainAlreadyAssigned,
    Failed
}

public interface IInstanceRepository
{
    Task<IReadOnlyList<ManagedInstance>> ListAsync(CancellationToken cancellationToken);

    Task<ManagedInstance?> FindAsync(string instanceId, CancellationToken cancellationToken);

    Task<CreateInstanceResult> CreateAsync(ManagedInstance instance, CancellationToken cancellationToken);

    Task UpdateStatusAsync(string instanceId, string status, CancellationToken cancellationToken);

    Task UpdateNginxMetadataAsync(string instanceId, int templateVersion, int configRevision, CancellationToken cancellationToken);
    Task UpdateImageAsync(string instanceId, string containerId, string imageReference, CancellationToken cancellationToken);

    Task RemoveAsync(string instanceId, CancellationToken cancellationToken);
}

public sealed class InstanceRepository(IAgentDatabase database, ILogger<InstanceRepository> logger) : IInstanceRepository
{
    public async Task<IReadOnlyList<ManagedInstance>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM instances ORDER BY created_at;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var instances = new List<ManagedInstance>();
        while (await reader.ReadAsync(cancellationToken))
        {
            instances.Add(ReadInstance(reader));
        }

        return instances;
    }

    public async Task<ManagedInstance?> FindAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM instances WHERE id = $id;";
        command.Parameters.AddWithValue("$id", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInstance(reader) : null;
    }

    public async Task<CreateInstanceResult> CreateAsync(ManagedInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO instances (
                    id, name, domain, container_id, container_name, host_port,
                    database_provider, database_server, database_port, database_user, database_name, database_ssl_mode,
                    status, data_path, image_reference, image_digest, installed_template_version, config_revision,
                    created_at, updated_at)
                VALUES (
                    $id, $name, $domain, $containerId, $containerName, $hostPort,
                    $databaseProvider, $databaseServer, $databasePort, $databaseUser, $databaseName, $databaseSslMode,
                    $status, $dataPath, $imageReference, $imageDigest, $installedTemplateVersion, $configRevision,
                    $createdAt, $updatedAt);
                """;
            AddParameters(command, instance);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return CreateInstanceResult.Created;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return CreateInstanceResult.DomainAlreadyAssigned;
        }
        catch (SqliteException exception)
        {
            logger.LogWarning(exception, "Could not persist instance metadata for {InstanceId}", instance.Id);
            return CreateInstanceResult.Failed;
        }
    }

    public async Task UpdateStatusAsync(string instanceId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE instances SET status = $status, updated_at = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$id", instanceId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateNginxMetadataAsync(string instanceId, int templateVersion, int configRevision, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE instances SET installed_template_version=$version, config_revision=$revision, updated_at=$updatedAt WHERE id=$id;";
        command.Parameters.AddWithValue("$id", instanceId);
        command.Parameters.AddWithValue("$version", templateVersion);
        command.Parameters.AddWithValue("$revision", configRevision);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateImageAsync(string instanceId, string containerId, string imageReference, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE instances SET container_id=$container, image_reference=$image, updated_at=$updatedAt WHERE id=$id;";
        command.Parameters.AddWithValue("$id", instanceId); command.Parameters.AddWithValue("$container", containerId); command.Parameters.AddWithValue("$image", imageReference); command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        // backups and events reference instances(id) via a plain FOREIGN KEY (no ON DELETE CASCADE),
        // so their rows must be removed first or the DELETE below fails with a constraint violation.
        foreach (var table in new[] { "backups", "events" })
        {
            await using var deleteChildren = connection.CreateCommand();
            deleteChildren.Transaction = transaction;
            deleteChildren.CommandText = $"DELETE FROM {table} WHERE instance_id = $id;";
            deleteChildren.Parameters.AddWithValue("$id", instanceId);
            await deleteChildren.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM instances WHERE id = $id;";
        command.Parameters.AddWithValue("$id", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        transaction.Commit();
    }

    private static void AddParameters(SqliteCommand command, ManagedInstance instance)
    {
        command.Parameters.AddWithValue("$id", instance.Id);
        command.Parameters.AddWithValue("$name", instance.Name);
        command.Parameters.AddWithValue("$domain", instance.Domain);
        command.Parameters.AddWithValue("$containerId", (object?)instance.ContainerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$containerName", (object?)instance.ContainerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$hostPort", instance.HostPort);
        command.Parameters.AddWithValue("$databaseProvider", instance.DatabaseProvider);
        command.Parameters.AddWithValue("$databaseServer", (object?)instance.DatabaseServer ?? DBNull.Value);
        command.Parameters.AddWithValue("$databasePort", (object?)instance.DatabasePort ?? DBNull.Value);
        command.Parameters.AddWithValue("$databaseUser", (object?)instance.DatabaseUser ?? DBNull.Value);
        command.Parameters.AddWithValue("$databaseName", (object?)instance.DatabaseName ?? DBNull.Value);
        command.Parameters.AddWithValue("$databaseSslMode", (object?)instance.DatabaseSslMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", instance.Status);
        command.Parameters.AddWithValue("$dataPath", instance.DataPath);
        command.Parameters.AddWithValue("$imageReference", (object?)instance.ImageReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$imageDigest", (object?)instance.ImageDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$installedTemplateVersion", instance.InstalledTemplateVersion);
        command.Parameters.AddWithValue("$configRevision", instance.ConfigRevision);
        command.Parameters.AddWithValue("$createdAt", instance.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", instance.UpdatedAt.ToString("O"));
    }

    private static ManagedInstance ReadInstance(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("name")),
        reader.GetString(reader.GetOrdinal("domain")),
        GetNullableString(reader, "container_id"),
        GetNullableString(reader, "container_name"),
        reader.GetInt32(reader.GetOrdinal("host_port")),
        reader.GetString(reader.GetOrdinal("database_provider")),
        GetNullableString(reader, "database_server"),
        GetNullableInt(reader, "database_port"),
        GetNullableString(reader, "database_user"),
        GetNullableString(reader, "database_name"),
        GetNullableString(reader, "database_ssl_mode"),
        reader.GetString(reader.GetOrdinal("status")),
        reader.GetString(reader.GetOrdinal("data_path")),
        GetNullableString(reader, "image_reference"),
        GetNullableString(reader, "image_digest"),
        reader.GetInt32(reader.GetOrdinal("installed_template_version")),
        reader.GetInt32(reader.GetOrdinal("config_revision")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static string? GetNullableString(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private static int? GetNullableInt(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column));
}
