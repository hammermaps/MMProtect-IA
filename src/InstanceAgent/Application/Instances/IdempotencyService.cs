using Microsoft.Data.Sqlite;
using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Application.Instances;

public sealed record IdempotencyRecord(int StatusCode, string ResponseBody);

public interface IIdempotencyService
{
    Task<IdempotencyRecord?> TryBeginAsync(string operation, string key, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(string operation, string key, int statusCode, string responseBody, CancellationToken cancellationToken);
}

public sealed class IdempotencyService(IAgentDatabase database) : IIdempotencyService
{
    public async Task<IdempotencyRecord?> TryBeginAsync(string operation, string key, string requestHash, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO idempotency_records (operation, key, request_hash, response_status, response_body, created_at, expires_at)
            VALUES ($operation, $key, $requestHash, 0, '{}', $createdAt, $expiresAt)
            ON CONFLICT(operation, key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$expiresAt", DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return null;
        }

        await using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT response_status, response_body FROM idempotency_records WHERE operation = $operation AND key = $key;";
        existing.Parameters.AddWithValue("$operation", operation);
        existing.Parameters.AddWithValue("$key", key);
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new IdempotencyRecord(reader.GetInt32(0), reader.GetString(1))
            : new IdempotencyRecord(0, "{}");
    }

    public async Task CompleteAsync(string operation, string key, int statusCode, string responseBody, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE idempotency_records SET response_status = $status, response_body = $body WHERE operation = $operation AND key = $key;";
        command.Parameters.AddWithValue("$status", statusCode);
        command.Parameters.AddWithValue("$body", responseBody);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
