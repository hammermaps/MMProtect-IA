using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Application.Backups;
using MmProtect.InstanceAgent.Models.Requests;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MmProtect.InstanceAgent.Options;
using Microsoft.Extensions.Options;

namespace MmProtect.InstanceAgent.Api;

public static class InstanceEndpoints
{
    public static IEndpointRouteBuilder MapInstanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/instances");
        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/start", (string id, HttpContext context, IInstanceLifecycleService service, CancellationToken cancellationToken) => ExecuteLifecycleAsync(id, InstanceLifecycleOperation.Start, context, service, cancellationToken));
        group.MapPost("/{id}/stop", (string id, HttpContext context, IInstanceLifecycleService service, CancellationToken cancellationToken) => ExecuteLifecycleAsync(id, InstanceLifecycleOperation.Stop, context, service, cancellationToken));
        group.MapPost("/{id}/restart", (string id, HttpContext context, IInstanceLifecycleService service, CancellationToken cancellationToken) => ExecuteLifecycleAsync(id, InstanceLifecycleOperation.Restart, context, service, cancellationToken));
        group.MapPost("/{id}/reset", ResetAsync);
        group.MapPost("/{id}/restore", RestoreAsync);
        return endpoints;
    }

    private static async Task<IResult> RestoreAsync(string id, IFormFile backup, HttpContext context, IInstanceRestoreService service, IOptions<AgentOptions> options, CancellationToken cancellationToken)
    {
        if (backup.Length == 0) return AgentProblem.Create(context, 400, "RESTORE_ARCHIVE_INVALID", "A non-empty backup archive is required.");
        if (backup.Length > options.Value.RestoreMaxArchiveBytes) return AgentProblem.Create(context, 413, "RESTORE_ARCHIVE_TOO_LARGE", "Backup archive exceeds the configured size limit.");
        await using var stream = backup.OpenReadStream();
        var result = await service.RestoreAsync(id, stream, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new { id, status = result.Status })
            : AgentProblem.Create(context, result.ErrorCode == "INSTANCE_NOT_FOUND" ? 404 : result.ErrorCode!.StartsWith("RESTORE_ARCHIVE", StringComparison.Ordinal) || result.ErrorCode.StartsWith("RESTORE_FORMAT", StringComparison.Ordinal) || result.ErrorCode.StartsWith("RESTORE_INSTANCE", StringComparison.Ordinal) || result.ErrorCode == "RESTORE_DATABASE_UNSUPPORTED" ? 400 : 409, result.ErrorCode, "Backup archive could not be restored.");
    }

    private static async Task<IResult> ResetAsync(string id, ResetInstanceRequest request, HttpContext context, IInstanceResetService service, CancellationToken cancellationToken)
    {
        var result = await service.ResetAsync(id, request, cancellationToken);
        return result.Succeeded ? Results.Ok(new { id, status = result.Status }) : AgentProblem.Create(context, 409, result.ErrorCode!, "Instance reset failed.");
    }

    private static async Task<IResult> CreateAsync(
        CreateInstanceRequest request,
        HttpContext context,
        ISqliteInstanceProvisioningService provisioningService,
        IIdempotencyService idempotencyService,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Database?.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return AgentProblem.Create(context, StatusCodes.Status400BadRequest, "DATABASE_PROVIDER_UNSUPPORTED", "Only the sqlite provider is currently available.");
        }

        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length > 128 || key.Any(char.IsWhiteSpace))
        {
            return AgentProblem.Create(context, StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_INVALID", "The Idempotency-Key header is invalid.");
        }

        if (!string.IsNullOrEmpty(key))
        {
            var existing = await idempotencyService.TryBeginAsync("create-instance", key, RequestHash(request), cancellationToken);
            if (existing is not null)
            {
                return existing.StatusCode == 0
                    ? AgentProblem.Create(context, StatusCodes.Status409Conflict, "IDEMPOTENCY_IN_PROGRESS", "The request is already being processed.")
                    : Results.Json(new { replayed = true, result = JsonSerializer.Deserialize<JsonElement>(existing.ResponseBody) }, statusCode: existing.StatusCode);
            }
        }

        var result = await provisioningService.ProvisionAsync(new CreateSqliteInstanceCommand(request.Name, request.Domain, request.Tls?.Mode), cancellationToken);
        if (!result.Succeeded)
        {
            if (!string.IsNullOrEmpty(key))
            {
                await idempotencyService.CompleteAsync("create-instance", key, StatusCodes.Status409Conflict, JsonSerializer.Serialize(new { error = result.ErrorCode }), cancellationToken);
            }

            return AgentProblem.Create(context, StatusCodes.Status409Conflict, result.ErrorCode!, "Instance provisioning failed.");
        }

        var safeResult = new { id = result.InstanceId, hostPort = result.HostPort, status = "running" };
        if (!string.IsNullOrEmpty(key))
        {
            await idempotencyService.CompleteAsync("create-instance", key, StatusCodes.Status201Created, JsonSerializer.Serialize(safeResult), cancellationToken);
        }

        return Results.Created($"/api/v1/instances/{result.InstanceId}", new
        {
            safeResult.id,
            safeResult.hostPort,
            safeResult.status,
            credentials = new { adminApiKey = result.Credentials!.AdminApiKey, encoderApiKey = result.Credentials.EncoderApiKey }
        });
    }

    private static string RequestHash(CreateInstanceRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Name}\n{request.Domain}\nsqlite")));

    private static async Task<IResult> ListAsync(IInstanceRepository repository, CancellationToken cancellationToken)
    {
        var instances = await repository.ListAsync(cancellationToken);
        return Results.Ok(instances.Select(ToResponse));
    }

    private static async Task<IResult> GetAsync(string id, IInstanceRepository repository, HttpContext context, CancellationToken cancellationToken)
    {
        var instance = await repository.FindAsync(id, cancellationToken);
        return instance is null
            ? AgentProblem.Create(context, StatusCodes.Status404NotFound, "INSTANCE_NOT_FOUND", "The requested instance does not exist.")
            : Results.Ok(ToResponse(instance));
    }

    private static async Task<IResult> ExecuteLifecycleAsync(string id, InstanceLifecycleOperation operation, HttpContext context, IInstanceLifecycleService service, CancellationToken cancellationToken)
    {
        var result = await service.ExecuteAsync(id, operation, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new { id, status = result.Status })
            : AgentProblem.Create(context, result.ErrorCode == "INSTANCE_NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, result.ErrorCode!, "Instance lifecycle operation failed.");
    }

    private static async Task<IResult> DeleteAsync(string id, bool? deleteData, HttpContext context, IInstanceDeletionService service, CancellationToken cancellationToken)
    {
        var result = await service.DeleteAsync(id, deleteData ?? false, cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : AgentProblem.Create(context, result.ErrorCode == "INSTANCE_NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, result.ErrorCode!, "Instance deletion failed.");
    }

    private static object ToResponse(ManagedInstance instance) => new
    {
        instance.Id,
        instance.Name,
        instance.Domain,
        hostPort = instance.HostPort,
        database = new
        {
            provider = instance.DatabaseProvider,
            server = instance.DatabaseServer,
            port = instance.DatabasePort,
            user = instance.DatabaseUser,
            name = instance.DatabaseName,
            sslMode = instance.DatabaseSslMode
        },
        instance.Status,
        instance.ContainerName,
        instance.ImageReference,
        instance.ImageDigest,
        nginx = new { installedTemplateVersion = instance.InstalledTemplateVersion, configRevision = instance.ConfigRevision },
        instance.CreatedAt,
        instance.UpdatedAt
    };
}
