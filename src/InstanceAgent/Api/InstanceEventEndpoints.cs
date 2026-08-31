using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Application.Instances;

namespace MmProtect.InstanceAgent.Api;

public static class InstanceEventEndpoints
{
    public static IEndpointRouteBuilder MapInstanceEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/instances/{instanceId}/events", GetAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(string instanceId, int? limit, int? offset, HttpContext context, IInstanceRepository instances, IAgentDatabase database, CancellationToken cancellationToken)
    {
        if (await instances.FindAsync(instanceId, cancellationToken) is null)
            return AgentProblem.Create(context, 404, "INSTANCE_NOT_FOUND", "The requested instance does not exist.");
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var skip = Math.Max(offset ?? 0, 0);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,event_type,message,created_at FROM events WHERE instance_id=$instance ORDER BY created_at DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$instance", instanceId);
        command.Parameters.AddWithValue("$limit", take);
        command.Parameters.AddWithValue("$offset", skip);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new { id = reader.GetString(0), type = reader.GetString(1), message = reader.GetString(2), createdAt = reader.GetString(3) });
        return Results.Ok(new { items = events, limit = take, offset = skip });
    }
}
