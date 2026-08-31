using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Application.Host;

public interface IInstanceEventService
{
    Task RecordAsync(string? instanceId, string eventType, string message, CancellationToken cancellationToken);
}

/// <summary>Writes short, secret-free audit events to the local agent database.</summary>
public sealed class InstanceEventService(IAgentDatabase database) : IInstanceEventService
{
    public async Task RecordAsync(string? instanceId, string eventType, string message, CancellationToken cancellationToken)
    {
        if (eventType.Length > 96 || message.Length > 512 || message.ContainsAny(['\r', '\n'])) throw new ArgumentException("Invalid event data.");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO events (id,instance_id,event_type,message,created_at) VALUES ($id,$instance,$type,$message,$createdAt);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
