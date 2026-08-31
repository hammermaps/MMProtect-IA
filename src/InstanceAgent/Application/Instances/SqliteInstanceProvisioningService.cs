using System.Text.RegularExpressions;
using System.Text.Json;
using MmProtect.InstanceAgent.Domain.Instances;
using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.LicenseServer;
using MmProtect.InstanceAgent.Infrastructure.Nginx;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Infrastructure.Tls;
using MmProtect.InstanceAgent.Options;
using Microsoft.Extensions.Options;

namespace MmProtect.InstanceAgent.Application.Instances;

public sealed record CreateSqliteInstanceCommand(string? Name, string? Domain, string? TlsMode);

public sealed record ProvisionInstanceResult(
    bool Succeeded,
    string? InstanceId,
    int? HostPort,
    InitialInstanceSecrets? Credentials,
    string? ErrorCode)
{
    public static ProvisionInstanceResult Failure(string errorCode) => new(false, null, null, null, errorCode);
}

public interface ISqliteInstanceProvisioningService
{
    Task<ProvisionInstanceResult> ProvisionAsync(CreateSqliteInstanceCommand command, CancellationToken cancellationToken);
}

public sealed partial class SqliteInstanceProvisioningService(
    IDomainNameValidator domainValidator,
    IPortAllocator portAllocator,
    IInstanceSecretStore secretStore,
    ISqliteLicenseServerDatabaseInitializer databaseInitializer,
    INginxService nginxService,
    ITlsCertificateService tlsCertificateService,
    IDockerService dockerService,
    IInstanceHealthProbe healthProbe,
    IInstanceRepository instanceRepository,
    IOptions<AgentOptions> options,
    ILogger<SqliteInstanceProvisioningService> logger) : ISqliteInstanceProvisioningService
{
    public async Task<ProvisionInstanceResult> ProvisionAsync(CreateSqliteInstanceCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 128 || !NamePattern().IsMatch(command.Name))
        {
            return ProvisionInstanceResult.Failure("INSTANCE_NAME_INVALID");
        }

        var domain = domainValidator.Validate(command.Domain);
        if (!domain.IsValid)
        {
            return ProvisionInstanceResult.Failure(domain.ErrorCode!);
        }

        var tlsMode = command.TlsMode?.ToLowerInvariant();
        if (tlsMode is not null and not "letsencrypt" and not "none")
        {
            return ProvisionInstanceResult.Failure("TLS_MODE_UNSUPPORTED");
        }
        var useTls = tlsMode == "letsencrypt";

        if (string.IsNullOrWhiteSpace(options.Value.LicenseServerImage) || options.Value.LicenseServerImage.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            return ProvisionInstanceResult.Failure("LICENSE_SERVER_IMAGE_NOT_CONFIGURED");
        }

        var availability = await dockerService.GetAvailabilityAsync(cancellationToken);
        if (!availability.Available)
        {
            return ProvisionInstanceResult.Failure(availability.ErrorCode ?? "DOCKER_UNAVAILABLE");
        }

        var instanceId = Guid.NewGuid().ToString("N");
        var reservation = await portAllocator.ReserveAsync(instanceId, cancellationToken);
        if (!reservation.Succeeded)
        {
            return ProvisionInstanceResult.Failure(reservation.ErrorCode!);
        }
        var hostPort = reservation.Port ?? throw new InvalidOperationException("Successful port reservation did not contain a port.");

        string? containerId = null;
        var committed = false;
        try
        {
            var credentials = await secretStore.CreateAsync(instanceId, cancellationToken);
            var instancePath = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "instances", instanceId);
            var dataPath = Path.Combine(instancePath, "data");
            Directory.CreateDirectory(dataPath);
            await databaseInitializer.InitializeAsync(Path.Combine(dataPath, "mm_license.db"), cancellationToken);
            await WriteInstanceMetadataAsync(instancePath, instanceId, command.Name!, domain.NormalizedDomain!, hostPort, cancellationToken);

            if (useTls)
            {
                var challenge = await nginxService.PrepareHttpChallengeAsync(instanceId, domain.NormalizedDomain!, cancellationToken);
                if (!challenge.Succeeded)
                {
                    return ProvisionInstanceResult.Failure(challenge.ErrorCode!);
                }

                var certificate = await tlsCertificateService.IssueLetsEncryptAsync(domain.NormalizedDomain!, cancellationToken);
                if (!certificate.Succeeded)
                {
                    return ProvisionInstanceResult.Failure(certificate.ErrorCode!);
                }
            }

            var containerName = $"mmprotect-{instanceId}";
            var create = await dockerService.CreateAsync(new CreateLicenseServerContainer(
                containerName,
                options.Value.LicenseServerImage,
                hostPort,
                [
                    "ASPNETCORE_HTTP_PORTS=8080",
                    "DatabaseProvider=sqlite",
                    "ConnectionStrings__Sqlite=Data Source=/data/mm_license.db",
                    $"Security__EncoderApiKeys__0={credentials.EncoderApiKey}",
                    $"Security__AdminApiKeys__0={credentials.AdminApiKey}",
                    $"Security__KeyEncryptionKey={credentials.KeyEncryptionKey}",
                    "Security__SigningPrivateKeyFile=/run/secrets/signing-private.pem",
                    "ReverseProxy__Enabled=true",
                    "ReverseProxy__ForwardLimit=1"
                ],
                new Dictionary<string, string>
                {
                    [dataPath] = "/data",
                    [Path.Combine(instancePath, "keys", "signing-private.pem")] = "/run/secrets/signing-private.pem:ro"
                }), cancellationToken);
            if (!create.Succeeded || create.ContainerId is null)
            {
                return ProvisionInstanceResult.Failure(create.ErrorCode ?? "DOCKER_CREATE_FAILED");
            }

            containerId = create.ContainerId;
            var start = await dockerService.StartAsync(containerId, cancellationToken);
            if (!start.Succeeded)
            {
                return ProvisionInstanceResult.Failure(start.ErrorCode ?? "DOCKER_START_FAILED");
            }

            if (!await healthProbe.IsHealthyAsync(hostPort, cancellationToken))
            {
                return ProvisionInstanceResult.Failure("INSTANCE_HEALTHCHECK_FAILED");
            }

            var nginx = useTls
                ? await nginxService.ApplyAsync(instanceId, domain.NormalizedDomain!, hostPort, cancellationToken)
                : await nginxService.ApplyHttpAsync(instanceId, domain.NormalizedDomain!, hostPort, cancellationToken);
            if (!nginx.Succeeded)
            {
                return ProvisionInstanceResult.Failure(nginx.ErrorCode!);
            }

            var now = DateTimeOffset.UtcNow;
            var persisted = await instanceRepository.CreateAsync(new ManagedInstance(
                instanceId, command.Name!, domain.NormalizedDomain!, containerId, containerName, hostPort,
                "sqlite", null, null, null, null, null, "running", instancePath, options.Value.LicenseServerImage,
                null, 0, 0, now, now), cancellationToken);
            if (persisted != CreateInstanceResult.Created)
            {
                return ProvisionInstanceResult.Failure(persisted == CreateInstanceResult.DomainAlreadyAssigned ? "DOMAIN_ALREADY_ASSIGNED" : "INSTANCE_PERSIST_FAILED");
            }

            logger.LogInformation("Provisioned SQLite instance {InstanceId} with result {Result}", instanceId, "running");
            committed = true;
            return new ProvisionInstanceResult(true, instanceId, hostPort, credentials, null);
        }
        finally
        {
            // A successful instance is persisted only after all earlier phases have completed.
            // Any early return above is rolled back by this guard.
            if (!committed)
            {
                if (containerId is not null)
                {
                    await dockerService.DeleteAsync(containerId, CancellationToken.None);
                }

                await nginxService.RemoveAsync(instanceId, CancellationToken.None);
                await secretStore.DeleteAsync(instanceId, CancellationToken.None);
                await portAllocator.ReleaseAsync(instanceId, CancellationToken.None);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,127}$")]
    private static partial Regex NamePattern();

    private static async Task WriteInstanceMetadataAsync(string instancePath, string instanceId, string name, string domain, int hostPort, CancellationToken cancellationToken)
    {
        var target = Path.Combine(instancePath, "instance.json");
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            id = instanceId,
            name,
            domain,
            hostPort,
            database = new { provider = "sqlite" },
            createdAt = DateTimeOffset.UtcNow
        }), cancellationToken);
        File.Move(temporary, target, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
