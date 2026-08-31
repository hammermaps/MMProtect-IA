using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Application.Backups;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MmProtect.InstanceAgent.Api;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/instances/{instanceId}/backups");
        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{backupId}", GetAsync);
        group.MapGet("/{backupId}/download", DownloadAsync);
        group.MapDelete("/{backupId}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(string instanceId, HttpContext context, IInstanceBackupService service, IIdempotencyService idempotencyService, CancellationToken cancellationToken)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length > 128 || key.Any(char.IsWhiteSpace))
        {
            return AgentProblem.Create(context, 400, "IDEMPOTENCY_KEY_INVALID", "The Idempotency-Key header is invalid.");
        }
        if (!string.IsNullOrEmpty(key))
        {
            var existing = await idempotencyService.TryBeginAsync($"create-backup:{instanceId}", key, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))), cancellationToken);
            if (existing is not null)
            {
                return existing.StatusCode == 0
                    ? AgentProblem.Create(context, 409, "IDEMPOTENCY_IN_PROGRESS", "The request is already being processed.")
                    : Results.Json(new { replayed = true, result = JsonSerializer.Deserialize<JsonElement>(existing.ResponseBody) }, statusCode: existing.StatusCode);
            }
        }
        var result = await service.CreateAsync(instanceId, cancellationToken);
        if (!result.Succeeded)
        {
            var status = result.ErrorCode == "INSTANCE_NOT_FOUND" ? 404 : 409;
            if (!string.IsNullOrEmpty(key)) await idempotencyService.CompleteAsync($"create-backup:{instanceId}", key, status, JsonSerializer.Serialize(new { error = result.ErrorCode }), cancellationToken);
            return AgentProblem.Create(context, status, result.ErrorCode!, "Backup creation failed.");
        }
        var response = new { id = result.BackupId, status = "completed" };
        if (!string.IsNullOrEmpty(key)) await idempotencyService.CompleteAsync($"create-backup:{instanceId}", key, 201, JsonSerializer.Serialize(response), cancellationToken);
        return Results.Created($"/api/v1/instances/{instanceId}/backups/{result.BackupId}", response);
    }

    private static async Task<IResult> ListAsync(string instanceId, IAgentDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,file_name,file_size,status,database_provider,created_at,completed_at FROM backups WHERE instance_id=$instanceId ORDER BY created_at DESC;";
        command.Parameters.AddWithValue("$instanceId", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new { id = reader.GetString(0), fileName = reader.GetString(1), fileSize = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2), status = reader.GetString(3), databaseProvider = reader.GetString(4), createdAt = reader.GetString(5), completedAt = reader.IsDBNull(6) ? null : reader.GetString(6) });
        return Results.Ok(results);
    }

    private static async Task<IResult> GetAsync(string instanceId, string backupId, HttpContext context, IAgentDatabase database, CancellationToken cancellationToken)
    {
        var backup = await FindAsync(database, instanceId, backupId, cancellationToken);
        return backup is null ? AgentProblem.Create(context, 404, "BACKUP_NOT_FOUND", "The requested backup does not exist.") : Results.Ok(backup.Value.Metadata);
    }

    private static async Task<IResult> DownloadAsync(string instanceId, string backupId, HttpContext context, IAgentDatabase database, CancellationToken cancellationToken)
    {
        var backup = await FindAsync(database, instanceId, backupId, cancellationToken);
        if (backup is null || !File.Exists(backup.Value.Path)) return AgentProblem.Create(context, 404, "BACKUP_NOT_FOUND", "The requested backup does not exist.");
        return Results.File(backup.Value.Path, "application/zip", backup.Value.FileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteAsync(string instanceId, string backupId, HttpContext context, IAgentDatabase database, CancellationToken cancellationToken)
    {
        var backup = await FindAsync(database, instanceId, backupId, cancellationToken);
        if (backup is null) return AgentProblem.Create(context, 404, "BACKUP_NOT_FOUND", "The requested backup does not exist.");
        try
        {
            if (File.Exists(backup.Value.Path)) File.Delete(backup.Value.Path);
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM backups WHERE id=$id AND instance_id=$instance;";
            command.Parameters.AddWithValue("$id", backupId); command.Parameters.AddWithValue("$instance", instanceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Results.NoContent();
        }
        catch (IOException)
        {
            return AgentProblem.Create(context, 409, "BACKUP_DELETE_FAILED", "Backup deletion failed.");
        }
    }

    private static async Task<(object Metadata, string Path, string FileName)?> FindAsync(IAgentDatabase database, string instanceId, string backupId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name,file_path,file_size,status,database_provider,created_at,completed_at FROM backups WHERE id=$id AND instance_id=$instance;";
        command.Parameters.AddWithValue("$id", backupId); command.Parameters.AddWithValue("$instance", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var fileName = reader.GetString(0); var path = reader.GetString(1);
        return (new { id = backupId, fileName, fileSize = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2), status = reader.GetString(3), databaseProvider = reader.GetString(4), createdAt = reader.GetString(5), completedAt = reader.IsDBNull(6) ? null : reader.GetString(6) }, path, fileName);
    }
}
