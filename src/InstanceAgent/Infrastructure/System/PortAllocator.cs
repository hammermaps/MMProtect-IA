using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Infrastructure.System;

public interface IPortAllocator
{
    Task<PortReservationResult> ReserveAsync(string instanceId, CancellationToken cancellationToken);

    Task ReleaseAsync(string instanceId, CancellationToken cancellationToken);
}

public sealed record PortReservationResult(bool Succeeded, int? Port, string? ErrorCode)
{
    public static PortReservationResult Exhausted() => new(false, null, "PORT_EXHAUSTED");

    public static PortReservationResult Failed() => new(false, null, "PORT_RESERVATION_FAILED");
}

public interface ILocalPortProbe
{
    bool CanBind(int port);
}

public sealed class LocalPortProbe : ILocalPortProbe
{
    public bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

public sealed class PortAllocator(
    IAgentDatabase database,
    IOptions<AgentOptions> options,
    ILocalPortProbe portProbe,
    ILogger<PortAllocator> logger) : IPortAllocator
{
    public async Task<PortReservationResult> ReserveAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("An instance ID is required.", nameof(instanceId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var transactionStarted = false;

        try
        {
            await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);
            transactionStarted = true;

            var existing = await GetReservedPortAsync(connection, instanceId, cancellationToken);
            if (existing.HasValue)
            {
                await ExecuteAsync(connection, "COMMIT;", cancellationToken);
                transactionStarted = false;
                return new PortReservationResult(true, existing, null);
            }

            for (var port = options.Value.PortRangeStart; port <= options.Value.PortRangeEnd; port++)
            {
                if (await IsReservedAsync(connection, port, cancellationToken) || !portProbe.CanBind(port))
                {
                    continue;
                }

                await InsertReservationAsync(connection, port, instanceId, cancellationToken);
                await ExecuteAsync(connection, "COMMIT;", cancellationToken);
                transactionStarted = false;
                logger.LogInformation("Reserved localhost port {HostPort} for instance {InstanceId}", port, instanceId);
                return new PortReservationResult(true, port, null);
            }

            await ExecuteAsync(connection, "COMMIT;", cancellationToken);
            transactionStarted = false;
            return PortReservationResult.Exhausted();
        }
        catch (SqliteException exception)
        {
            logger.LogWarning(exception, "Port reservation failed for instance {InstanceId}", instanceId);
            return PortReservationResult.Failed();
        }
        finally
        {
            if (transactionStarted)
            {
                await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None);
            }
        }
    }

    public async Task ReleaseAsync(string instanceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ports WHERE instance_id = $instanceId;";
        command.Parameters.AddWithValue("$instanceId", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Released reserved port for instance {InstanceId}", instanceId);
    }

    private static async Task<int?> GetReservedPortAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT port FROM ports WHERE instance_id = $instanceId;";
        command.Parameters.AddWithValue("$instanceId", instanceId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long port ? checked((int)port) : null;
    }

    private static async Task<bool> IsReservedAsync(SqliteConnection connection, int port, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM ports WHERE port = $port);";
        command.Parameters.AddWithValue("$port", port);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task InsertReservationAsync(SqliteConnection connection, int port, string instanceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ports (port, instance_id, reserved_at) VALUES ($port, $instanceId, $reservedAt);";
        command.Parameters.AddWithValue("$port", port);
        command.Parameters.AddWithValue("$instanceId", instanceId);
        command.Parameters.AddWithValue("$reservedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
