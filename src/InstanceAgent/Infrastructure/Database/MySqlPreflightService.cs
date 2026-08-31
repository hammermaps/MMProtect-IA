using System.Net;
using System.Net.Sockets;
using MySqlConnector;
using MmProtect.InstanceAgent.Models.Requests;

namespace MmProtect.InstanceAgent.Infrastructure.Database;

public sealed record MySqlPreflightResult(bool Succeeded, string? ErrorCode, string? ServerVersion, long? LatencyMs);

public interface IMySqlPreflightService
{
    Task<MySqlPreflightResult> CheckAsync(InstanceDatabaseRequest? request, CancellationToken cancellationToken);
}

public sealed class MySqlPreflightService : IMySqlPreflightService
{
    public async Task<MySqlPreflightResult> CheckAsync(InstanceDatabaseRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.User) ||
            string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.Database) ||
            request.Port is not (> 0 and <= 65535) || !TryParseSslMode(request.SslMode, out var sslMode))
        {
            return new MySqlPreflightResult(false, "MYSQL_PREFLIGHT_FAILED", null, null);
        }

        var started = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Dns.GetHostAddressesAsync(request.Server, cancellationToken);
        }
        catch (SocketException)
        {
            return new MySqlPreflightResult(false, "MYSQL_DNS_FAILED", null, null);
        }

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(request.Server, request.Port.Value, cancellationToken);
        }
        catch (SocketException)
        {
            return new MySqlPreflightResult(false, "MYSQL_PORT_UNREACHABLE", null, null);
        }
        catch (OperationCanceledException)
        {
            return new MySqlPreflightResult(false, "MYSQL_HOST_UNREACHABLE", null, null);
        }

        try
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = request.Server,
                Port = (uint)request.Port.Value,
                UserID = request.User,
                Password = request.Password,
                Database = request.Database,
                SslMode = sslMode,
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 10,
                Pooling = false
            };
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            started.Stop();
            return new MySqlPreflightResult(true, null, connection.ServerVersion, started.ElapsedMilliseconds);
        }
        catch (MySqlException exception) when (exception.Number == 1045)
        {
            return new MySqlPreflightResult(false, "MYSQL_AUTH_FAILED", null, null);
        }
        catch (MySqlException exception) when (exception.Number == 1049)
        {
            return new MySqlPreflightResult(false, "MYSQL_DATABASE_NOT_FOUND", null, null);
        }
        catch (MySqlException)
        {
            return new MySqlPreflightResult(false, sslMode != MySqlSslMode.None ? "MYSQL_TLS_FAILED" : "MYSQL_QUERY_FAILED", null, null);
        }
        catch (OperationCanceledException)
        {
            return new MySqlPreflightResult(false, "MYSQL_HOST_UNREACHABLE", null, null);
        }
    }

    private static bool TryParseSslMode(string? value, out MySqlSslMode sslMode) =>
        Enum.TryParse(value, ignoreCase: true, out sslMode) && Enum.IsDefined(sslMode);
}
