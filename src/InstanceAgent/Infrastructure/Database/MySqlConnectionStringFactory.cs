using MySqlConnector;
using MmProtect.InstanceAgent.Models.Requests;

namespace MmProtect.InstanceAgent.Infrastructure.Database;

public interface IMySqlConnectionStringFactory
{
    string Create(InstanceDatabaseRequest request, string password);
}

public sealed class MySqlConnectionStringFactory : IMySqlConnectionStringFactory
{
    public string Create(InstanceDatabaseRequest request, string password)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.User) || string.IsNullOrWhiteSpace(request.Database) ||
            request.Port is not (> 0 and <= 65535) || !Enum.TryParse<MySqlSslMode>(request.SslMode, true, out var sslMode) || !Enum.IsDefined(sslMode))
        {
            throw new ArgumentException("Invalid MySQL connection settings.", nameof(request));
        }

        return new MySqlConnectionStringBuilder
        {
            Server = request.Server,
            Port = (uint)request.Port.Value,
            UserID = request.User,
            Password = password,
            Database = request.Database,
            SslMode = sslMode,
            AllowPublicKeyRetrieval = true,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 10
        }.ConnectionString;
    }
}
