using MmProtect.InstanceAgent.Application.Backups;
using MmProtect.InstanceAgent.Models.Requests;

namespace MmProtect.InstanceAgent.Application.Instances;

public interface IInstanceResetService { Task<InstanceResetResult> ResetAsync(string instanceId, ResetInstanceRequest request, CancellationToken cancellationToken); }
public sealed record InstanceResetResult(bool Succeeded, string? ErrorCode, string? Status);

public sealed class InstanceResetService(IInstanceBackupService backups, IInstanceDeletionService deletion, IInstanceLifecycleService lifecycle) : IInstanceResetService
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
        var deleted = await deletion.DeleteAsync(instanceId, deleteData: true, cancellationToken);
        return new(deleted.Succeeded, deleted.ErrorCode, deleted.Succeeded ? "deleted" : null);
    }
}
