using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Models.Requests;

namespace MmProtect.InstanceAgent.Api;

public static class PreflightEndpoints
{
    public static IEndpointRouteBuilder MapPreflightEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/preflight", CheckAllAsync);
        endpoints.MapGet("/api/v1/preflight/docker", CheckDockerAsync);
        endpoints.MapGet("/api/v1/preflight/nginx", CheckNginxAsync);
        endpoints.MapPost("/api/v1/preflight/mysql", CheckMySqlAsync);
        return endpoints;
    }

    private static async Task<IResult> CheckAllAsync(IDockerService dockerService, IProcessRunner runner, CancellationToken cancellationToken)
    {
        var docker = await dockerService.GetAvailabilityAsync(cancellationToken);
        var nginx = await runner.RunAsync("nginx", ["-t"], TimeSpan.FromSeconds(15), cancellationToken);
        return Results.Ok(new
        {
            status = docker.Available && nginx.Succeeded ? "ready" : "degraded",
            docker = new { available = docker.Available, serverVersion = docker.ServerVersion, errorCode = docker.ErrorCode },
            nginx = new { available = nginx.Succeeded, timedOut = nginx.TimedOut, exitCode = nginx.ExitCode }
        });
    }

    private static async Task<IResult> CheckDockerAsync(IDockerService service, CancellationToken cancellationToken)
    {
        var result = await service.GetAvailabilityAsync(cancellationToken);
        return Results.Ok(new { available = result.Available, serverVersion = result.ServerVersion, errorCode = result.ErrorCode });
    }

    private static async Task<IResult> CheckNginxAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("nginx", ["-t"], TimeSpan.FromSeconds(15), cancellationToken);
        return Results.Ok(new { available = result.Succeeded, timedOut = result.TimedOut, exitCode = result.ExitCode });
    }

    private static async Task<IResult> CheckMySqlAsync(InstanceDatabaseRequest request, HttpContext context, IMySqlPreflightService service, CancellationToken cancellationToken)
    {
        var result = await service.CheckAsync(request, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new { reachable = true, authenticated = true, databaseExists = true, result.ServerVersion, result.LatencyMs })
            : AgentProblem.Create(context, StatusCodes.Status400BadRequest, result.ErrorCode!, "MySQL preflight failed.");
    }
}
