namespace MmProtect.InstanceAgent.Models.Requests;

public sealed class CreateInstanceRequest
{
    public string? Name { get; init; }

    public string? Domain { get; init; }

    public InstanceDatabaseRequest? Database { get; init; }

    public InstanceTlsRequest? Tls { get; init; }
}

public sealed class InstanceTlsRequest
{
    public string? Mode { get; init; }
}

public sealed class InstanceDatabaseRequest
{
    public string? Provider { get; init; }

    public string? Server { get; init; }

    public int? Port { get; init; }

    public string? User { get; init; }

    public string? Password { get; init; }

    public string? Database { get; init; }

    public string? SslMode { get; init; }
}
