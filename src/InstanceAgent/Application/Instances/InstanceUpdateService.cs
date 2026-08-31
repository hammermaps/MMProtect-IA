using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Options;
using MmProtect.InstanceAgent.Application.Host;
using MmProtect.InstanceAgent.Infrastructure.Nginx;
using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Application.Backups;

namespace MmProtect.InstanceAgent.Application.Instances;

public interface IInstanceUpdateService
{
    Task<InstanceUpdateCheckResult> CheckAsync(string instanceId, CancellationToken cancellationToken);

    Task<InstanceUpdateApplyResult> ApplyNginxAsync(string instanceId, CancellationToken cancellationToken);
    Task<InstanceUpdateApplyResult> ApplyLicenseServerAsync(string instanceId, CancellationToken cancellationToken);
}

public sealed record InstanceUpdateCheckResult(
    bool Succeeded,
    string? ErrorCode,
    string? CurrentImage,
    string? AvailableImage,
    bool LicenseServerUpdateAvailable,
    int InstalledNginxTemplateVersion,
    int AvailableNginxTemplateVersion,
    bool NginxUpdateAvailable);
public sealed record InstanceUpdateApplyResult(bool Succeeded, string? ErrorCode, int? TemplateVersion, int? ConfigRevision);

/// <summary>Determines updates solely from locally configured, versioned artifacts. It never resolves floating tags.</summary>
public sealed class InstanceUpdateService(IInstanceRepository instances, INginxService nginx, IInstanceEventService events, IDockerService docker, IInstanceHealthProbe health, IInstanceSecretStore secrets, IMySqlConnectionStringFactory connectionStrings, IInstanceBackupService backups, IOptions<AgentOptions> options) : IInstanceUpdateService
{
    public async Task<InstanceUpdateCheckResult> CheckAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken);
        if (instance is null) return new(false, "INSTANCE_NOT_FOUND", null, null, false, 0, 0, false);

        var configuredImage = options.Value.LicenseServerImage;
        var imageUpdateAvailable = !string.IsNullOrWhiteSpace(configuredImage) &&
            !string.Equals(instance.ImageReference, configuredImage, StringComparison.Ordinal);
        var nginxUpdateAvailable = instance.InstalledTemplateVersion < options.Value.NginxTemplateVersion;
        return new(true, null, instance.ImageReference, configuredImage, imageUpdateAvailable,
            instance.InstalledTemplateVersion, options.Value.NginxTemplateVersion, nginxUpdateAvailable);
    }

    public async Task<InstanceUpdateApplyResult> ApplyNginxAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken);
        if (instance is null) return new(false, "INSTANCE_NOT_FOUND", null, null);
        if (instance.Status == "deleted") return new(false, "INSTANCE_RUNTIME_DELETED", null, null);
        var configPath = Path.Combine(options.Value.NginxConfigurationDirectory, $"{instanceId}.conf");
        if (!File.Exists(configPath)) return new(false, "NGINX_CONFIG_MISSING", null, null);
        var existing = await File.ReadAllTextAsync(configPath, cancellationToken);
        var result = existing.Contains("ssl_certificate", StringComparison.Ordinal)
            ? await nginx.ApplyAsync(instanceId, instance.Domain, instance.HostPort, cancellationToken)
            : await nginx.ApplyHttpAsync(instanceId, instance.Domain, instance.HostPort, cancellationToken);
        if (!result.Succeeded) return new(false, result.ErrorCode, null, null);
        var revision = Math.Max(instance.ConfigRevision + 1, result.ConfigRevision);
        await instances.UpdateNginxMetadataAsync(instanceId, result.TemplateVersion, revision, cancellationToken);
        await events.RecordAsync(instanceId, "update.nginx.completed", $"Applied nginx template version {result.TemplateVersion}.", cancellationToken);
        return new(true, null, result.TemplateVersion, revision);
    }

    public async Task<InstanceUpdateApplyResult> ApplyLicenseServerAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken);
        var targetImage = options.Value.LicenseServerImage;
        if (instance is null) return new(false, "INSTANCE_NOT_FOUND", null, null);
        if (string.IsNullOrWhiteSpace(targetImage) || targetImage.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)) return new(false, "LICENSE_SERVER_IMAGE_NOT_CONFIGURED", null, null);
        if (string.Equals(instance.ImageReference, targetImage, StringComparison.Ordinal)) return new(true, null, null, null);
        if (string.IsNullOrWhiteSpace(instance.ContainerId) || string.IsNullOrWhiteSpace(instance.ImageReference)) return new(false, "INSTANCE_CONTAINER_MISSING", null, null);
        var backup = await backups.CreateAsync(instanceId, cancellationToken);
        if (!backup.Succeeded) return new(false, "UPDATE_BACKUP_FAILED", null, null);
        var runtimeSecrets = await secrets.GetRuntimeSecretsAsync(instanceId, cancellationToken);
        if (runtimeSecrets is null) return new(false, "INSTANCE_SECRETS_MISSING", null, null);
        var oldImage = instance.ImageReference;
        var oldContainer = instance.ContainerId;
        var deleted = await docker.DeleteAsync(oldContainer, cancellationToken);
        if (!deleted.Succeeded) return new(false, deleted.ErrorCode, null, null);
        var created = await CreateAndStartAsync(instance, targetImage, runtimeSecrets, cancellationToken);
        if (created.Succeeded && created.ContainerId is not null && await health.IsHealthyAsync(instance.HostPort, cancellationToken))
        {
            await instances.UpdateImageAsync(instanceId, created.ContainerId, targetImage, cancellationToken);
            await events.RecordAsync(instanceId, "update.license-server.completed", "Updated LicenseServer after a pre-update backup.", cancellationToken);
            return new(true, null, null, null);
        }
        if (created.ContainerId is not null) await docker.DeleteAsync(created.ContainerId, CancellationToken.None);
        var rollback = await CreateAndStartAsync(instance, oldImage, runtimeSecrets, CancellationToken.None);
        return new(false, rollback.Succeeded ? "UPDATE_HEALTHCHECK_FAILED_ROLLED_BACK" : "UPDATE_ROLLBACK_FAILED", null, null);
    }

    private async Task<DockerOperationResult> CreateAndStartAsync(ManagedInstance instance, string image, RuntimeInstanceSecrets runtime, CancellationToken cancellationToken)
    {
        var instancePath = Path.GetDirectoryName(instance.DataPath)!;
        var environment = new List<string> { "ASPNETCORE_HTTP_PORTS=8080", $"DatabaseProvider={instance.DatabaseProvider}", $"Security__EncoderApiKeys__0={runtime.EncoderApiKey}", $"Security__AdminApiKeys__0={runtime.AdminApiKey}", $"Security__KeyEncryptionKey={runtime.KeyEncryptionKey}", "Security__SigningPrivateKeyFile=/run/secrets/signing-private.pem", "ReverseProxy__Enabled=true", "ReverseProxy__ForwardLimit=1" };
        if (instance.DatabaseProvider == "sqlite") environment.Add("ConnectionStrings__Sqlite=Data Source=/data/mm_license.db");
        else { var password = await secrets.GetMySqlPasswordAsync(instance.Id, cancellationToken); if (string.IsNullOrWhiteSpace(password)) return DockerOperationResult.Failure("MYSQL_SECRET_MISSING"); environment.Add($"ConnectionStrings__MySql={connectionStrings.Create(new Models.Requests.InstanceDatabaseRequest { Server = instance.DatabaseServer, Port = instance.DatabasePort, User = instance.DatabaseUser, Database = instance.DatabaseName, SslMode = instance.DatabaseSslMode }, password)}"); }
        var create = await docker.CreateAsync(new CreateLicenseServerContainer(instance.ContainerName!, image, instance.HostPort, environment, new Dictionary<string, string> { [instance.DataPath] = "/data", [Path.Combine(instancePath, "keys", "signing-private.pem")] = "/run/secrets/signing-private.pem:ro" }), cancellationToken);
        return !create.Succeeded || create.ContainerId is null ? create : await docker.StartAsync(create.ContainerId, cancellationToken);
    }
}
