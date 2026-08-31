using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.Nginx;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Infrastructure.System;

namespace MmProtect.InstanceAgent.Application.Instances;

public interface IInstanceDeletionService
{
    Task<InstanceDeletionResult> DeleteAsync(string instanceId, bool deleteData, CancellationToken cancellationToken);
}

public sealed record InstanceDeletionResult(bool Succeeded, string? ErrorCode);

public sealed class InstanceDeletionService(
    IInstanceRepository repository,
    IDockerService dockerService,
    INginxService nginxService,
    IPortAllocator portAllocator,
    IInstanceSecretStore secretStore,
    ILogger<InstanceDeletionService> logger) : IInstanceDeletionService
{
    public async Task<InstanceDeletionResult> DeleteAsync(string instanceId, bool deleteData, CancellationToken cancellationToken)
    {
        var instance = await repository.FindAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return new InstanceDeletionResult(false, "INSTANCE_NOT_FOUND");
        }

        if (!string.IsNullOrWhiteSpace(instance.ContainerId))
        {
            var remove = await dockerService.DeleteAsync(instance.ContainerId, cancellationToken);
            if (!remove.Succeeded)
            {
                return new InstanceDeletionResult(false, remove.ErrorCode);
            }
        }

        var nginx = await nginxService.RemoveAsync(instanceId, cancellationToken);
        if (!nginx.Succeeded)
        {
            return new InstanceDeletionResult(false, nginx.ErrorCode);
        }

        await portAllocator.ReleaseAsync(instanceId, cancellationToken);
        if (deleteData)
        {
            await secretStore.DeleteAsync(instanceId, cancellationToken);
            await repository.RemoveAsync(instanceId, cancellationToken);
        }
        else
        {
            await repository.UpdateStatusAsync(instanceId, "deleted", cancellationToken);
        }

        logger.LogInformation("Deleted instance runtime for {InstanceId}; data deleted: {DeleteData}", instanceId, deleteData);
        return new InstanceDeletionResult(true, null);
    }
}
