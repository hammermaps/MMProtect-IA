using MmProtect.InstanceAgent.Application.Backups;
using MmProtect.InstanceAgent.Models.Requests;
using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Application.Instances;

public interface IInstanceResetService { Task<InstanceResetResult> ResetAsync(string instanceId, ResetInstanceRequest request, CancellationToken cancellationToken); }
public sealed record InstanceResetResult(bool Succeeded, string? ErrorCode, string? Status);

public sealed class InstanceResetService(IInstanceBackupService backups, IInstanceDeletionService deletion, IInstanceLifecycleService lifecycle, IAgentDatabase database) : IInstanceResetService
{
    public async Task<InstanceResetResult> ResetAsync(string instanceId, ResetInstanceRequest request, CancellationToken cancellationToken)
    {
        var mode = request.Mode?.ToLowerInvariant();
        if (mode == "runtime")
        {
            var result = await lifecycle.ExecuteAsync(instanceId, InstanceLifecycleOperation.Restart, cancellationToken);
            return new(result.Succeeded, result.ErrorCode, result.Status);
        }
        if (mode != "full") return new(false, "RESET_MODE_INVALID", null);
        if (!string.Equals(request.Confirmation, "RESET", StringComparison.Ordinal)) return new(false, "RESET_CONFIRMATION_REQUIRED", null);
        if (request.CreateBackup ?? true)
        {
            var backup = await backups.CreateAsync(instanceId, cancellationToken);
            if (!backup.Succeeded) return new(false, "RESET_BACKUP_FAILED", null);
        }
        var backupPaths = request.DeleteBackups == true ? await FindBackupPathsAsync(instanceId, cancellationToken) : [];
        var deleted = await deletion.DeleteAsync(instanceId, deleteData: true, cancellationToken);
        if (deleted.Succeeded && request.DeleteBackups == true)
        {
            try
            {
                foreach (var path in backupPaths)
                    if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                return new(false, "RESET_BACKUP_DELETE_FAILED", null);
            }
        }
        return new(deleted.Succeeded, deleted.ErrorCode, deleted.Succeeded ? "deleted" : null);
    }

    private async Task<IReadOnlyList<string>> FindBackupPathsAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM backups WHERE instance_id=$instance;";
        command.Parameters.AddWithValue("$instance", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var paths = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) paths.Add(reader.GetString(0));
        return paths;
    }
}
