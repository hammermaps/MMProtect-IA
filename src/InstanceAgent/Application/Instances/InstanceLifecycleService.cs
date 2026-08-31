using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Models.Requests;

namespace MmProtect.InstanceAgent.Application.Instances;

public interface IInstanceLifecycleService
{
    Task<InstanceLifecycleResult> ExecuteAsync(string instanceId, InstanceLifecycleOperation operation, CancellationToken cancellationToken);
}

public enum InstanceLifecycleOperation { Start, Stop, Restart }

public sealed record InstanceLifecycleResult(bool Succeeded, string? ErrorCode, string? Status);

public sealed class InstanceLifecycleService(
    IInstanceRepository repository,
    IDockerService dockerService,
    IMySqlPreflightService mySqlPreflightService,
    IInstanceSecretStore secretStore,
    ILogger<InstanceLifecycleService> logger) : IInstanceLifecycleService
{
    public async Task<InstanceLifecycleResult> ExecuteAsync(string instanceId, InstanceLifecycleOperation operation, CancellationToken cancellationToken)
    {
        var instance = await repository.FindAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return new InstanceLifecycleResult(false, "INSTANCE_NOT_FOUND", null);
        }

        if (string.IsNullOrWhiteSpace(instance.ContainerId))
        {
            return new InstanceLifecycleResult(false, "INSTANCE_CONTAINER_MISSING", null);
        }

        if ((operation is InstanceLifecycleOperation.Start or InstanceLifecycleOperation.Restart) &&
            string.Equals(instance.DatabaseProvider, "mysql", StringComparison.OrdinalIgnoreCase))
        {
            var password = await secretStore.GetMySqlPasswordAsync(instanceId, cancellationToken);
            if (string.IsNullOrWhiteSpace(password))
            {
                return new InstanceLifecycleResult(false, "MYSQL_SECRET_MISSING", null);
            }

            var preflight = await mySqlPreflightService.CheckAsync(new InstanceDatabaseRequest
            {
                Provider = "mysql", Server = instance.DatabaseServer, Port = instance.DatabasePort,
                User = instance.DatabaseUser, Password = password, Database = instance.DatabaseName, SslMode = instance.DatabaseSslMode
            }, cancellationToken);
            if (!preflight.Succeeded)
            {
                return new InstanceLifecycleResult(false, preflight.ErrorCode, null);
            }
        }

        var result = operation switch
        {
            InstanceLifecycleOperation.Start => await dockerService.StartAsync(instance.ContainerId, cancellationToken),
            InstanceLifecycleOperation.Stop => await dockerService.StopAsync(instance.ContainerId, cancellationToken),
            InstanceLifecycleOperation.Restart => await dockerService.RestartAsync(instance.ContainerId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (!result.Succeeded)
        {
            return new InstanceLifecycleResult(false, result.ErrorCode, null);
        }

        var status = operation == InstanceLifecycleOperation.Stop ? "stopped" : "running";
        await repository.UpdateStatusAsync(instanceId, status, cancellationToken);
        logger.LogInformation("Instance lifecycle {Operation} completed for {InstanceId}", operation, instanceId);
        return new InstanceLifecycleResult(true, null, status);
    }
}
