using MmProtect.InstanceAgent.Application.Host;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Infrastructure.Backup;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Options;
using Microsoft.Extensions.Options;

namespace MmProtect.InstanceAgent.Application.Backups;

public interface IInstanceBackupService { Task<InstanceBackupResult> CreateAsync(string instanceId, CancellationToken cancellationToken); }
public sealed record InstanceBackupResult(bool Succeeded, string? BackupId, string? ErrorCode);

public sealed class InstanceBackupService(IInstanceRepository instances, IInstanceAgentIdentityService identity, ISqliteBackupProvider sqlite, IMySqlDumpProvider mySql, IInstanceSecretStore secrets, IZipBackupWriter zip, IAgentDatabase database, IInstanceEventService events, IOptions<AgentOptions> options) : IInstanceBackupService
{
    public async Task<InstanceBackupResult> CreateAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken);
        if (instance is null) return new(false, null, "INSTANCE_NOT_FOUND");
        if (instance.DatabaseProvider is not "sqlite" and not "mysql") return new(false, null, "BACKUP_PROVIDER_UNSUPPORTED");
        var backupId = Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(options.Value.DataDirectory);
        var isMySql = string.Equals(instance.DatabaseProvider, "mysql", StringComparison.OrdinalIgnoreCase);
        var snapshot = Path.Combine(root, "tmp", isMySql ? $"{backupId}.sql" : $"{backupId}.db");
        var output = Path.Combine(root, "backups", instanceId, $"mmprotect-{instanceId}-{DateTimeOffset.UtcNow:yyyy-MM-dd_HHmmss}.zip");
        try
        {
            if (isMySql)
            {
                var password = await secrets.GetMySqlPasswordAsync(instanceId, cancellationToken);
                if (string.IsNullOrWhiteSpace(password)) return new(false, null, "MYSQL_SECRET_MISSING");
                var dump = await mySql.CreateDumpAsync(instance, password, snapshot, cancellationToken);
                if (!dump.Succeeded) return new(false, null, dump.ErrorCode);
            }
            else
            {
                var snap = await sqlite.CreateSnapshotAsync(Path.Combine(instance.DataPath, "mm_license.db"), snapshot, cancellationToken);
                if (!snap.Succeeded) return new(false, null, snap.ErrorCode);
            }
            var package = await zip.CreateAsync(new(await identity.GetOrCreateAsync(cancellationToken), instanceId, instance.Name, instance.Domain, Path.Combine(instance.DataPath, "..", "instance.json"), instance.DataPath, snapshot, Path.Combine(instance.DataPath, "..", "keys", "signing-public.pem"), output, instance.DatabaseProvider, isMySql ? "mysql.sql" : "mm_license.db"), cancellationToken);
            if (!package.Succeeded) return new(false, null, package.ErrorCode);
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO backups (id,instance_id,file_name,file_path,file_size,status,database_provider,created_at,completed_at) VALUES ($id,$instance,$name,$path,$size,'completed',$provider,$now,$now);";
            command.Parameters.AddWithValue("$id", backupId); command.Parameters.AddWithValue("$instance", instanceId);
            command.Parameters.AddWithValue("$name", Path.GetFileName(output)); command.Parameters.AddWithValue("$path", output);
            command.Parameters.AddWithValue("$size", package.FileSize!.Value); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$provider", instance.DatabaseProvider);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await events.RecordAsync(instanceId, "backup.created", $"Created {instance.DatabaseProvider} backup {backupId}.", cancellationToken);
            return new(true, backupId, null);
        }
        finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
    }
}
