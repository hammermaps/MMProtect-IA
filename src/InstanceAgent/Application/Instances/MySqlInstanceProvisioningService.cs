using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Application.Host;
using MmProtect.InstanceAgent.Domain.Instances;
using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.Nginx;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Infrastructure.Tls;
using MmProtect.InstanceAgent.Models.Requests;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Application.Instances;

public sealed record CreateMySqlInstanceCommand(string? Name, string? Domain, string? TlsMode, InstanceDatabaseRequest? Database);
public interface IMySqlInstanceProvisioningService { Task<ProvisionInstanceResult> ProvisionAsync(CreateMySqlInstanceCommand command, CancellationToken cancellationToken); }

public sealed partial class MySqlInstanceProvisioningService(
    IDomainNameValidator domainValidator, IPortAllocator portAllocator, IInstanceSecretStore secretStore,
    INginxService nginxService, ITlsCertificateService tlsCertificateService, IDockerService dockerService,
    IInstanceHealthProbe healthProbe, IMySqlPreflightService preflightService, IMySqlConnectionStringFactory connectionStrings,
    IInstanceRepository instances, IInstanceEventService events, IOptions<AgentOptions> options, ILogger<MySqlInstanceProvisioningService> logger) : IMySqlInstanceProvisioningService
{
    public async Task<ProvisionInstanceResult> ProvisionAsync(CreateMySqlInstanceCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 128 || !NamePattern().IsMatch(command.Name)) return ProvisionInstanceResult.Failure("INSTANCE_NAME_INVALID");
        var domain = domainValidator.Validate(command.Domain);
        if (!domain.IsValid) return ProvisionInstanceResult.Failure(domain.ErrorCode!);
        if (command.Database is null || !string.Equals(command.Database.Provider, "mysql", StringComparison.OrdinalIgnoreCase)) return ProvisionInstanceResult.Failure("MYSQL_PREFLIGHT_FAILED");
        var tlsMode = command.TlsMode?.ToLowerInvariant();
        if (tlsMode is not null and not "letsencrypt" and not "none") return ProvisionInstanceResult.Failure("TLS_MODE_UNSUPPORTED");
        if (string.IsNullOrWhiteSpace(options.Value.LicenseServerImage) || options.Value.LicenseServerImage.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)) return ProvisionInstanceResult.Failure("LICENSE_SERVER_IMAGE_NOT_CONFIGURED");
        var preflight = await preflightService.CheckAsync(command.Database, cancellationToken);
        if (!preflight.Succeeded) return ProvisionInstanceResult.Failure(preflight.ErrorCode!);
        var docker = await dockerService.GetAvailabilityAsync(cancellationToken);
        if (!docker.Available) return ProvisionInstanceResult.Failure(docker.ErrorCode ?? "DOCKER_UNAVAILABLE");

        var instanceId = Guid.NewGuid().ToString("N");
        var reservation = await portAllocator.ReserveAsync(instanceId, cancellationToken);
        if (!reservation.Succeeded) return ProvisionInstanceResult.Failure(reservation.ErrorCode!);
        var hostPort = reservation.Port!.Value;
        string? containerId = null;
        var committed = false;
        try
        {
            var credentials = await secretStore.CreateAsync(instanceId, cancellationToken);
            await secretStore.StoreMySqlPasswordAsync(instanceId, command.Database.Password!, cancellationToken);
            var instancePath = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "instances", instanceId);
            var dataPath = Path.Combine(instancePath, "data");
            Directory.CreateDirectory(dataPath);
            await WriteInstanceMetadataAsync(instancePath, instanceId, command.Name!, domain.NormalizedDomain!, hostPort, command.Database, cancellationToken);
            var useTls = tlsMode == "letsencrypt";
            if (useTls)
            {
                var challenge = await nginxService.PrepareHttpChallengeAsync(instanceId, domain.NormalizedDomain!, cancellationToken);
                if (!challenge.Succeeded) return ProvisionInstanceResult.Failure(challenge.ErrorCode!);
                var certificate = await tlsCertificateService.IssueLetsEncryptAsync(domain.NormalizedDomain!, cancellationToken);
                if (!certificate.Succeeded) return ProvisionInstanceResult.Failure(certificate.ErrorCode!);
            }
            var connectionString = connectionStrings.Create(command.Database, command.Database.Password!);
            var create = await dockerService.CreateAsync(new CreateLicenseServerContainer($"mmprotect-{instanceId}", options.Value.LicenseServerImage, hostPort,
                ["ASPNETCORE_HTTP_PORTS=8080", "DatabaseProvider=mysql", $"ConnectionStrings__MySql={connectionString}",
                 $"Security__EncoderApiKeys__0={credentials.EncoderApiKey}", $"Security__AdminApiKeys__0={credentials.AdminApiKey}",
                 $"Security__KeyEncryptionKey={credentials.KeyEncryptionKey}", "Security__SigningPrivateKeyFile=/run/secrets/signing-private.pem",
                 "ReverseProxy__Enabled=true", "ReverseProxy__ForwardLimit=1"],
                new Dictionary<string, string> { [dataPath] = "/data", [Path.Combine(instancePath, "keys", "signing-private.pem")] = "/run/secrets/signing-private.pem:ro" }), cancellationToken);
            if (!create.Succeeded || create.ContainerId is null) return ProvisionInstanceResult.Failure(create.ErrorCode ?? "DOCKER_CREATE_FAILED");
            containerId = create.ContainerId;
            var start = await dockerService.StartAsync(containerId, cancellationToken);
            if (!start.Succeeded) return ProvisionInstanceResult.Failure(start.ErrorCode ?? "DOCKER_START_FAILED");
            if (!await healthProbe.IsHealthyAsync(hostPort, cancellationToken)) return ProvisionInstanceResult.Failure("INSTANCE_HEALTHCHECK_FAILED");
            var nginx = useTls ? await nginxService.ApplyAsync(instanceId, domain.NormalizedDomain!, hostPort, cancellationToken) : await nginxService.ApplyHttpAsync(instanceId, domain.NormalizedDomain!, hostPort, cancellationToken);
            if (!nginx.Succeeded) return ProvisionInstanceResult.Failure(nginx.ErrorCode!);
            var now = DateTimeOffset.UtcNow;
            var persisted = await instances.CreateAsync(new ManagedInstance(instanceId, command.Name!, domain.NormalizedDomain!, containerId, $"mmprotect-{instanceId}", hostPort,
                "mysql", command.Database.Server, command.Database.Port, command.Database.User, command.Database.Database, command.Database.SslMode,
                "running", dataPath, options.Value.LicenseServerImage, null, nginx.TemplateVersion, nginx.ConfigRevision, now, now), cancellationToken);
            if (persisted != CreateInstanceResult.Created) return ProvisionInstanceResult.Failure(persisted == CreateInstanceResult.DomainAlreadyAssigned ? "DOMAIN_ALREADY_ASSIGNED" : "INSTANCE_PERSIST_FAILED");
            committed = true;
            logger.LogInformation("Provisioned MySQL instance {InstanceId} with result {Result}", instanceId, "running");
            await events.RecordAsync(instanceId, "provisioning.completed", "Provisioned MySQL instance.", cancellationToken);
            return new(true, instanceId, hostPort, credentials, null);
        }
        finally
        {
            if (!committed)
            {
                if (containerId is not null) await dockerService.DeleteAsync(containerId, CancellationToken.None);
                await nginxService.RemoveAsync(instanceId, CancellationToken.None);
                await secretStore.DeleteAsync(instanceId, CancellationToken.None);
                await portAllocator.ReleaseAsync(instanceId, CancellationToken.None);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,127}$")]
    private static partial Regex NamePattern();

    private static async Task WriteInstanceMetadataAsync(string path, string id, string name, string domain, int hostPort, InstanceDatabaseRequest database, CancellationToken cancellationToken)
    {
        var target = Path.Combine(path, "instance.json");
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { id, name, domain, hostPort, database = new { provider = "mysql", server = database.Server, port = database.Port, user = database.User, name = database.Database, sslMode = database.SslMode }, createdAt = DateTimeOffset.UtcNow }), cancellationToken);
        File.Move(temporary, target, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
