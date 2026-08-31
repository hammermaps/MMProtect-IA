namespace MmProtect.InstanceAgent.Api;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/agent/version", () => Results.Ok(new
        {
            installedVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            apiVersion = "v1"
        }));
        endpoints.MapGet("/api/v1/agent/update", () => Results.Ok(new
        {
            installedVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            availableVersion = (string?)null,
            updateAvailable = false,
            selfUpdateSupported = false
        }));
        return endpoints;
    }
}
